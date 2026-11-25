using System.Net.Http.Json;
using Chk3.Shared;

// API Autorização (CDE). Sem banco. PAN só em memória durante a requisição, nunca logado nem persistido.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });

var internalKey = Env.Required("INTERNAL_API_KEY");
var integrationUrl = Env.Required("INTEGRATION_INTERNAL_URL").TrimEnd('/');
var gatewayUrl = Env.Required("GATEWAY_URL").TrimEnd('/');
var gatewayKey = Env.Required("GATEWAY_API_KEY");
var gatewayTimeout = TimeSpan.FromMilliseconds(int.Parse(Env.Optional("GATEWAY_TIMEOUT_MS", "3000")));
var testPansOnly = Env.Optional("POC_TEST_PANS_ONLY", "true") == "true";
var signer = new SnapshotSigner(Env.Secret("SNAPSHOT_SIGNING_KEY"));

builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(integrationUrl); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddHttpClient("gateway", c => { c.BaseAddress = new Uri(gatewayUrl); c.DefaultRequestHeaders.Add("X-Gateway-Key", gatewayKey); c.Timeout = Timeout.InfiniteTimeSpan; });

var app = builder.Build();

// Allowlist de PANs de teste (POC). PAN fora dela é recusado no 9a, nunca chega ao Gateway.
string[] testPans = ["4111111111111111", "5555555555554444", "4000000000000002", "5105105105105100", "4000000000000119"];

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/authorize", async (HttpContext ctx, AuthorizeRequest? req, IHttpClientFactory http, ILogger<Program> log) =>
{
    if (!Web.FixedTimeEquals(ctx.Request.Headers[Web.InternalKeyHeader], internalKey)) return Results.StatusCode(401);
    string? typingCredential = ctx.Request.Headers[Web.TypingCredentialHeader];
    if (req is null || string.IsNullOrEmpty(typingCredential)) return Invalid("requisicao_invalida");

    // ---- Passo 9: verifica assinatura, sessão e expiração do snapshot; revalida Luhn, validade, CVV e bandeira ----
    var snap = signer.Verify(req.Snapshot, DateTimeOffset.UtcNow);
    if (snap is null) return Invalid("snapshot_invalido");                            // 9a
    var pan = req.Pan;
    var brand = pan is not null && pan.All(char.IsAsciiDigit) && Card.Luhn(pan) ? Card.Brand(pan) : null;
    var now = DateTime.UtcNow;
    bool expOk = req.ExpMonth is >= 1 and <= 12 && req.ExpYear is >= 2000 and <= 2099
                 && new DateTime(req.ExpYear, req.ExpMonth, 1).AddMonths(1) > now;
    bool cvvOk = req.Cvv is { Length: 3 } && req.Cvv.All(char.IsAsciiDigit);
    if (brand is null || !expOk || !cvvOk) { pan = null; return Invalid("cartao_invalido"); }        // 9a
    if (!snap.Brands.Contains(brand)) { pan = null; return Invalid("bandeira_nao_aceita"); }        // 9a
    if (req.Installments < 1 || req.Installments > snap.MaxInstallments) { pan = null; return Invalid("parcelas_invalidas"); }
    if (testPansOnly && !testPans.Contains(pan)) { pan = null; return Invalid("pan_nao_permitido_no_poc"); }
    var deviceSessionId = req.DeviceSessionId is { Length: > 0 and <= 128 } d && d.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? d : null;

    // ---- Passo 10: tentativa única atômica na Integration, sem PAN ----
    var integration = http.CreateClient("integration");
    using var attemptReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/attempts")
    {
        Content = JsonContent.Create(new { transactionId = snap.Sid, brand, installments = req.Installments }),
    };
    attemptReq.Headers.Add(Web.TypingCredentialHeader, typingCredential);
    using var attemptResp = await integration.SendAsync(attemptReq);
    if ((int)attemptResp.StatusCode == 409)
    {
        pan = null; // 10b: estado conhecido, sem novo envio
        var known = await attemptResp.Content.ReadFromJsonAsync<KnownState>();
        return Results.Json(new { status = "TentativaJaRegistrada", state = known?.State, transactionId = snap.Sid }, statusCode: 409);
    }
    if (!attemptResp.IsSuccessStatusCode) { pan = null; return Results.Json(new { status = "SessaoInvalida" }, statusCode: 422); } // 10a
    var attempt = (await attemptResp.Content.ReadFromJsonAsync<Attempt>())!;

    // ---- Passos 11 e 12: autorização no Gateway com valor do servidor. Timeout -> indeterminado, sem reenvio ----
    string result; string? code = null;
    try
    {
        using var cts = new CancellationTokenSource(gatewayTimeout);
        // TODO(v7 §4): campos reais do Gateway MCN e campo do deviceSessionId. Contrato abaixo é do mock.
        using var gwResp = await http.CreateClient("gateway").PostAsJsonAsync("/poc/v1/authorize", new
        {
            reference = attempt.LocalRef, amountCents = attempt.AmountCents, currency = attempt.Currency,
            installments = attempt.Installments,
            card = new { number = pan, expMonth = req.ExpMonth, expYear = req.ExpYear, cvv = req.Cvv },
            deviceSessionId,
        }, cts.Token);
        var gw = gwResp.IsSuccessStatusCode ? await gwResp.Content.ReadFromJsonAsync<GatewayResult>(cts.Token) : null;
        (result, code) = gw?.Result switch
        {
            "approved" => ("Autorizada", gw.Code),
            "declined" => ("Recusada", gw.Code),
            _ => ("Indeterminada", null), // resposta inesperada: não afirma resultado
        };
    }
    catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
    {
        result = "Indeterminada"; // 12b
        log.LogWarning("Gateway sem resposta no prazo para localRef {LocalRef}", attempt.LocalRef);
    }
    finally
    {
        pan = null; // 12: descarta PAN. Limitação: string .NET imutável, a memória só é liberada pelo GC.
    }

    // ---- Passo 13: resultado na Integration, sem PAN ----
    var registered = false;
    for (var i = 0; i < 3 && !registered; i++)
    {
        try
        {
            using var r = await integration.PostAsJsonAsync($"/internal/v1/attempts/{attempt.LocalRef}/result", new { result, gatewayCode = code });
            registered = r.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { await Task.Delay(200 * (i + 1)); }
    }
    if (!registered)
    {
        // Worker (passo 15) reconciliaria pela localRef. Fora do POC.
        log.LogError("Falha ao registrar resultado da localRef {LocalRef}", attempt.LocalRef);
        result = "Indeterminada";
    }

    // ---- Passo 14 ----
    return Results.Ok(new { status = result, transactionId = attempt.TransactionId });

    static IResult Invalid(string code) => Results.Json(new { status = "ValidacaoFalhou", erro = code }, statusCode: 422);
});

app.Run();

record AuthorizeRequest(string? Pan, int ExpMonth, int ExpYear, string? Cvv, int Installments, string? Snapshot, string? DeviceSessionId);
record Attempt(Guid TransactionId, Guid LocalRef, long AmountCents, string Currency, int Installments, string OrderId);
record KnownState(string? State);
record GatewayResult(string? Result, string? Code);

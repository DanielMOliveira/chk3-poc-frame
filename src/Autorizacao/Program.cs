using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Chk3.Autorizacao;
using Chk3.Shared;

// API Autorização (CDE). Sem banco. PAN só em memória durante a requisição, nunca logado nem persistido.
var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("autorizacao");
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });

var internalKey = Env.Required("INTERNAL_API_KEY");
var integrationUrl = Env.Required("INTEGRATION_INTERNAL_URL").TrimEnd('/');
// Gateway: GATEWAY_URL é o endpoint completo (o mock ou o real). Os PANs de simulação do mock vão SEMPRE para o mock
// (GATEWAY_MOCK_URL), para smoke/CI nunca gerarem transação no Gateway real. Só os PANs de teste do Gateway
// (POC_GATEWAY_TEST_PANS) seguem para GATEWAY_URL.
var gatewayUrl = new Uri(Env.Required("GATEWAY_URL"));
var mockUrl = new Uri(Env.Optional("GATEWAY_MOCK_URL", "http://gateway-mock:8080/poc/v1/authorize"));
var gatewayKey = Env.Required("GATEWAY_API_KEY"); // chave do MOCK: só vai no header quando o destino é o mock
var gatewayContentType = Env.Optional("GATEWAY_CONTENT_TYPE", "application/xml");
var mockTimeout = TimeSpan.FromMilliseconds(int.Parse(Env.Optional("GATEWAY_TIMEOUT_MS", "3000")));
var realTimeout = TimeSpan.FromMilliseconds(int.Parse(Env.Optional("GATEWAY_REAL_TIMEOUT_MS", "15000")));
string[] gatewayTestPans = Env.Optional("POC_GATEWAY_TEST_PANS", "5453010000066167").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var testPansOnly = Env.Optional("POC_TEST_PANS_ONLY", "true") == "true";
var signer = new SnapshotSigner(Env.Secret("SNAPSHOT_SIGNING_KEY"));
// Merchant no Gateway (passo 11). Só do .env. A merchantKey nunca é logada.
var merchant = new GatewayMerchant(
    MerchantId: Env.Required("GATEWAY_MERCHANT_ID"),
    MerchantKey: Env.Required("GATEWAY_MERCHANT_KEY"),
    Acquirer: Env.Optional("GATEWAY_ACQUIRER", "1") switch
    {
        var a and ("1" or "20" or "22") => a, // 1 = CIELO, 20 = STONE, 22 = GETNET
        _ => throw new InvalidOperationException("GATEWAY_ACQUIRER deve ser 1 (CIELO), 20 (STONE) ou 22 (GETNET)"),
    },
    SoftDescriptor: Env.Optional("GATEWAY_SOFT_DESCRIPTOR", "CHK3POC"),
    MethodInstallments: Env.Optional("GATEWAY_METHOD_PARCELADO", "2"),
    SaveCreditCard: Env.Optional("GATEWAY_SAVE_CREDIT_CARD", "false") == "true");
var holderRx = new Regex(@"^[\p{L}\p{M} .'-]{2,60}$");

builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(integrationUrl); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddHttpClient("gateway", c => { c.Timeout = Timeout.InfiniteTimeSpan; });

var app = builder.Build();
app.UseChk3Trace();

// Allowlist de PANs de teste (POC). PAN fora dela é recusado no 9a, nunca chega ao Gateway.
// mockPans: simulação do mock (aprova/recusa/timeout), sempre para o mock. gatewayTestPans: cartões de teste do Gateway.
string[] mockPans = ["4111111111111111", "5555555555554444", "4000000000000002", "5105105105105100", "4000000000000119"];
string[] testPans = [.. mockPans, .. gatewayTestPans];

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var gwLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("chk3.gateway");

app.MapPost("/authorize", async (HttpContext ctx, AuthorizeRequest? req, IHttpClientFactory http, ILogger<Program> log) =>
{
    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    Guid? txId = null; // conhecido depois de verificar o snapshot
    if (!Web.FixedTimeEquals(ctx.Request.Headers[Web.InternalKeyHeader], internalKey)) return Results.StatusCode(401);
    string? typingCredential = ctx.Request.Headers[Web.TypingCredentialHeader];
    if (req is null || string.IsNullOrEmpty(typingCredential)) return Invalid("requisicao_invalida");

    // ---- Passo 9: verifica assinatura, sessão e expiração do snapshot; revalida Luhn, validade, CVV e bandeira ----
    var snap = signer.Verify(req.Snapshot, DateTimeOffset.UtcNow);
    if (snap is null) return Invalid("snapshot_invalido");                            // 9a
    txId = snap.Sid;
    Chk3Trace.SetCorrelation(ctx, snap.Sid); // daqui em diante o X-Correlation-Id é o transactionId
    var pan = req.Pan;
    var brand = pan is not null && pan.All(char.IsAsciiDigit) && Card.Luhn(pan) ? Card.Brand(pan) : null;
    var now = DateTime.UtcNow;
    bool expOk = req.ExpMonth is >= 1 and <= 12 && req.ExpYear is >= 2000 and <= 2099
                 && new DateTime(req.ExpYear, req.ExpMonth, 1).AddMonths(1) > now;
    bool cvvOk = req.Cvv is { Length: 3 } && req.Cvv.All(char.IsAsciiDigit);
    if (brand is null || !expOk || !cvvOk) { pan = null; return Invalid("cartao_invalido"); }        // 9a
    if (!snap.Brands.Contains(brand)) { pan = null; return Invalid("bandeira_nao_aceita"); }        // 9a
    if (req.Installments < 1 || req.Installments > snap.MaxInstallments) { pan = null; return Invalid("parcelas_invalidas"); } // 1..máximo da sessão
    if (testPansOnly && !testPans.Contains(pan)) { pan = null; return Invalid("pan_nao_permitido_no_poc"); }
    if (req.HolderName is not { } holder || !holderRx.IsMatch(holder)) { pan = null; return Invalid("nome_invalido"); } // 9a: <cardHolder>
    Chk3Trace.Step(ctx, "9", txId, "validacao_ok", null, Chk3Trace.Since(t0));
    // deviceSessionId (passo 8) chega aqui, mas o modelo de XML do Gateway não tem campo para ele: não é enviado (pendência v7 §4).

    // ---- Passo 10: tentativa única atômica na Integration, sem PAN ----
    var integration = http.CreateClient("integration");
    using var attemptReq = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/attempts")
    {
        Content = JsonContent.Create(new { transactionId = snap.Sid, brand, installments = req.Installments }),
    };
    attemptReq.Headers.Add(Web.TypingCredentialHeader, typingCredential);
    var t10 = System.Diagnostics.Stopwatch.GetTimestamp();
    using var attemptResp = await integration.SendAsync(attemptReq);
    if ((int)attemptResp.StatusCode == 409)
    {
        pan = null; // 10b: estado conhecido, sem novo envio
        var known = await attemptResp.Content.ReadFromJsonAsync<KnownState>();
        Chk3Trace.Step(ctx, "10", txId, "10b_ja_registrada_" + known?.State, 409, Chk3Trace.Since(t10));
        return Results.Json(new { status = "TentativaJaRegistrada", state = known?.State, transactionId = snap.Sid }, statusCode: 409);
    }
    if (!attemptResp.IsSuccessStatusCode)
    {
        pan = null;
        Chk3Trace.Step(ctx, "10", txId, "10a_sessao_invalida", (int)attemptResp.StatusCode, Chk3Trace.Since(t10));
        return Results.Json(new { status = "SessaoInvalida", transactionId = snap.Sid }, statusCode: 422); // 10a
    }
    var attempt = (await attemptResp.Content.ReadFromJsonAsync<Attempt>())!;
    Chk3Trace.Step(ctx, "10", txId, "10c_registrada", (int)attemptResp.StatusCode, Chk3Trace.Since(t10), attempt.LocalRef, attempt.OrderId);

    // ---- Passos 11 e 12: autorização no Gateway com valor do servidor. Timeout -> indeterminado, sem reenvio ----
    string result; string? code = null; int? gwStatus = null; string formato = "-";
    var toMock = mockPans.Contains(pan) || gatewayUrl == mockUrl;
    var target = toMock ? mockUrl : gatewayUrl;
    var destino = toMock ? "mock" : "real";
    var t11 = System.Diagnostics.Stopwatch.GetTimestamp();
    try
    {
        using var cts = new CancellationTokenSource(toMock ? mockTimeout : realTimeout);
        // <transaction-request> (formato do Gateway, 26/11). Tem PAN, CVV e merchantKey: só a versão MASCARADA vai para o log.
        var xml = GatewayXml.Build(merchant,
            new GatewayOrder(attempt.OrderId, attempt.AmountCents, attempt.Currency, attempt.Installments, attempt.Billing, attempt.UrlReturn, attempt.SoftDescriptor),
            new GatewayCard(pan!, holder, req.Cvv!, req.ExpMonth, req.ExpYear, brand!));
        GwLog("11", "ida", null, GatewayLogMask.Mask(xml, merchant.MerchantKey, merchant.MerchantId), 0);
        using var msg = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(xml, Encoding.UTF8) };
        msg.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(gatewayContentType);
        if (msg.Content.Headers.ContentType.CharSet is null) msg.Content.Headers.ContentType.CharSet = "utf-8";
        if (toMock) msg.Headers.Add("X-Gateway-Key", gatewayKey); // chave do mock nunca vai para o Gateway real
        xml = null;
        using var gwResp = await http.CreateClient("gateway").SendAsync(msg, cts.Token);
        gwStatus = (int)gwResp.StatusCode;
        var body = await ReadLimitedAsync(gwResp.Content, 64 * 1024, cts.Token);
        var doc = SafeXml.TryParse(body);
        formato = doc?.Root?.Name.LocalName ?? (string.IsNullOrWhiteSpace(body) ? "vazio" : "nao_xml");
        GwLog("12", "volta", gwStatus, GatewayLogMask.Mask(body, merchant.MerchantKey, merchant.MerchantId), Chk3Trace.Since(t11),
            gwResp.Content.Headers.ContentType?.MediaType);
        var gw = gwResp.IsSuccessStatusCode ? GatewayXml.ParseResponse(doc) : (null, null);
        (result, code) = gw.Status switch
        {
            "approved" => ("Autorizada", gw.Code),
            "declined" => ("Recusada", gw.Code),
            _ => ("Indeterminada", null), // formato desconhecido ou erro HTTP: não afirma resultado (estrutura mascarada no log)
        };
    }
    catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
    {
        result = "Indeterminada"; // 12b
        GwLog("12", "volta", null, ex is OperationCanceledException ? "timeout" : "erro_http:" + ex.GetType().Name, Chk3Trace.Since(t11));
        log.LogWarning("Gateway sem resposta no prazo para localRef {LocalRef} transactionId {TransactionId}", attempt.LocalRef, attempt.TransactionId);
    }
    finally
    {
        pan = null; // 12: descarta PAN. Limitação: string .NET imutável, a memória só é liberada pelo GC.
    }
    Chk3Trace.Step(ctx, "11-12", txId, gwStatus is null ? $"{result}_timeout_12b_{destino}"
        : code is not null ? $"{result}_codigo_{code}_{destino}" : $"{result}_{destino}_http{gwStatus}_{formato}", gwStatus, Chk3Trace.Since(t11), attempt.LocalRef);
    var t13 = System.Diagnostics.Stopwatch.GetTimestamp();

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
    Chk3Trace.Step(ctx, "13", txId, registered ? result + "_registrado" : "falha_registro", null, Chk3Trace.Since(t13), attempt.LocalRef);
    if (!registered)
    {
        // Worker (passo 15) reconciliaria pela localRef. Fora do POC.
        log.LogError("Falha ao registrar resultado da localRef {LocalRef} transactionId {TransactionId}", attempt.LocalRef, attempt.TransactionId);
        result = "Indeterminada";
    }

    // ---- Passo 14 ----
    return Results.Ok(new { status = result, transactionId = attempt.TransactionId });

    // Linha "chk3gw" para o Grafana: XML de ida/volta do Gateway, SEMPRE mascarado (GatewayLogMask).
    void GwLog(string passo, string direcao, int? status, string xmlMascarado, long ms, string? contentType = null) =>
        gwLog.LogInformation("chk3gw passo={passo} servico=autorizacao transactionId={transactionId} localRef={localRef} destino={destino} direcao={direcao} status={status} contentType={contentType} duracaoMs={duracaoMs} xml={xml}",
            passo, txId, attempt.LocalRef, destino, direcao, status, contentType, ms, xmlMascarado);

    // 9a: código do erro e transactionId (se o snapshot já foi verificado). Nunca dado de cartão.
    IResult Invalid(string code)
    {
        Chk3Trace.Step(ctx, "9", txId, "9a_" + code, 422, Chk3Trace.Since(t0));
        return Results.Json(new { status = "ValidacaoFalhou", erro = code, transactionId = txId }, statusCode: 422);
    }
});

app.Run();

static async Task<string> ReadLimitedAsync(HttpContent c, int max, CancellationToken ct)
{
    await using var s = await c.ReadAsStreamAsync(ct);
    var buf = new byte[max];
    int n = 0, r;
    while (n < max && (r = await s.ReadAsync(buf.AsMemory(n, max - n), ct)) > 0) n += r;
    return Encoding.UTF8.GetString(buf, 0, n);
}

record AuthorizeRequest(string? Pan, string? HolderName, int ExpMonth, int ExpYear, string? Cvv, int Installments, string? Snapshot, string? DeviceSessionId);
record Attempt(Guid TransactionId, Guid LocalRef, long AmountCents, string Currency, int Installments, string OrderId,
    Dictionary<string, string>? Billing, string? UrlReturn, string? SoftDescriptor);
record KnownState(string? State);

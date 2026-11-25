using Chk3.Shared;

// Mock do Gateway MCN. CONTRATO INVENTADO PARA O POC: nomes de campos, códigos e formato são
// placeholders. TODO(v7 §4): contrato real do Gateway (campos, códigos de recusa, timeout,
// campo do deviceSessionId, dado de cartão na resposta 12).
var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("gateway-mock");
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });
var app = builder.Build();
app.UseChk3Trace();

var apiKey = Env.Required("GATEWAY_API_KEY");
var timeoutDelay = TimeSpan.FromMilliseconds(int.Parse(Env.Optional("GATEWAY_MOCK_TIMEOUT_DELAY_MS", "10000")));

// Só PANs de teste. Qualquer outro (inclusive real) é recusado sem processamento.
var testCards = new Dictionary<string, string>
{
    ["4111111111111111"] = "approve",
    ["5555555555554444"] = "approve",
    ["4000000000000002"] = "decline",
    ["5105105105105100"] = "decline",
    ["4000000000000119"] = "timeout",
};

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/poc/v1/authorize", async (HttpContext ctx, AuthorizeRequest? req) =>
{
    if (!Web.FixedTimeEquals(ctx.Request.Headers["X-Gateway-Key"], apiKey)) return Results.StatusCode(401);
    // O mock não recebe o transactionId no contrato: ele vem do X-Correlation-Id enviado pela API Autorização.
    Guid? tx = Guid.TryParse(Chk3Trace.Current, out var g) ? g : null;
    Guid? localRef = Guid.TryParse(req?.Reference, out var lr) ? lr : null;
    if (req?.Card?.Number is not { } pan || !testCards.TryGetValue(pan, out var behavior))
    {
        Chk3Trace.Mark(ctx, "11-12", tx, "pan_nao_e_de_teste", localRef);
        return Results.Json(new { erro = "pan_nao_e_de_teste" }, statusCode: 422);
    }

    switch (behavior)
    {
        case "timeout":
            try { await Task.Delay(timeoutDelay, ctx.RequestAborted); }
            catch (OperationCanceledException)
            {
                Chk3Trace.Mark(ctx, "11-12", tx, "timeout_simulado_cliente_desistiu", localRef); // 12b do lado do Gateway
                return Results.StatusCode(499);
            }
            Chk3Trace.Mark(ctx, "11-12", tx, "approved_00_atrasado", localRef);
            return Results.Ok(new { result = "approved", code = "00" }); // o cliente já desistiu (12b)
        case "decline":
            Chk3Trace.Mark(ctx, "11-12", tx, "declined_05", localRef);
            return Results.Ok(new { result = "declined", code = "05" });
        default:
            Chk3Trace.Mark(ctx, "11-12", tx, "approved_00", localRef);
            return Results.Ok(new { result = "approved", code = "00", authorizationCode = "POC" + Random.Shared.Next(100000, 999999) });
    }
});

app.Run();

record CardData(string? Number, int ExpMonth, int ExpYear, string? Cvv);
record AuthorizeRequest(string? Reference, long AmountCents, string? Currency, int Installments, CardData? Card, string? DeviceSessionId);

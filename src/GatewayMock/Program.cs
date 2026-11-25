using Chk3.Shared;

// Mock do Gateway MCN. CONTRATO INVENTADO PARA O POC: nomes de campos, códigos e formato são
// placeholders. TODO(v7 §4): contrato real do Gateway (campos, códigos de recusa, timeout,
// campo do deviceSessionId, dado de cartão na resposta 12).
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });
var app = builder.Build();

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
    if (req?.Card?.Number is not { } pan || !testCards.TryGetValue(pan, out var behavior))
        return Results.Json(new { erro = "pan_nao_e_de_teste" }, statusCode: 422);

    switch (behavior)
    {
        case "timeout":
            await Task.Delay(timeoutDelay, ctx.RequestAborted);
            return Results.Ok(new { result = "approved", code = "00" }); // o cliente já desistiu (12b)
        case "decline":
            return Results.Ok(new { result = "declined", code = "05" });
        default:
            return Results.Ok(new { result = "approved", code = "00", authorizationCode = "POC" + Random.Shared.Next(100000, 999999) });
    }
});

app.Run();

record CardData(string? Number, int ExpMonth, int ExpYear, string? Cvv);
record AuthorizeRequest(string? Reference, long AmountCents, string? Currency, int Installments, CardData? Card, string? DeviceSessionId);

using System.Net.Http.Json;
using System.Text;
using Chk3.Shared;

// Tela de opções (endpoint checkout, fora da CDE). Recebe a credencial de pagamento pelo fragment,
// o JS a guarda em memória e a envia no header. Este servidor só repassa à Integration (passos 6 e 7).
var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("opcoes");
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 8 * 1024; });

var internalKey = Env.Required("INTERNAL_API_KEY");
var canalOrigin = Env.Required("CANAL_PUBLIC_ORIGIN").TrimEnd('/');
var cartaoOrigin = Env.Required("CARTAO_PUBLIC_ORIGIN").TrimEnd('/');
builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(Env.Required("INTEGRATION_INTERNAL_URL")); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromSeconds(5); });

var app = builder.Build();
app.UseChk3Trace();

// frame-ancestors restrito ao canal (passo 6b). frame-src/form-action só para o endpoint pay.
app.UseSecurityHeaders($"default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; frame-src {cartaoOrigin}; form-action {cartaoOrigin}; base-uri 'none'; frame-ancestors {canalOrigin}");
app.UseRejectQueryString();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// Config pública para o JS (origens, sem segredos).
app.MapGet("/config.json", () => Results.Ok(new { cartaoOrigin, canalOrigin }));

// ---- Passo 6: valida a credencial de pagamento sem consumir ----
app.MapPost("/api/session", (HttpContext ctx, IHttpClientFactory http) =>
    Forward(ctx, http, "6", "/internal/v1/payment-credential/validate", null));

// ---- Passo 7: revalida e pede credencial de digitação ----
app.MapPost("/api/typing-credential", (HttpContext ctx, IHttpClientFactory http) =>
    Forward(ctx, http, "7", "/internal/v1/typing-credential", new { method = "cartao_credito" }));

app.Run();

static async Task<IResult> Forward(HttpContext ctx, IHttpClientFactory http, string passo, string path, object? body)
{
    string pc = ctx.Request.Headers[Web.PaymentCredentialHeader].ToString();
    if (pc.Length is 0 or > 128) { Chk3Trace.Mark(ctx, passo, null, "sem_credencial"); return Results.Json(new { erro = "recusada" }, statusCode: 401); }
    using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
    req.Headers.Add(Web.PaymentCredentialHeader, pc);
    using var resp = await http.CreateClient("integration").SendAsync(req);
    if (!resp.IsSuccessStatusCode)
    {
        Chk3Trace.Mark(ctx, passo, null, (passo == "6" ? "6a_" : "") + "recusada_integration_" + (int)resp.StatusCode);
        return Results.Json(new { erro = "recusada" }, statusCode: 401); // 6a sem detalhes
    }
    var json = await resp.Content.ReadAsStringAsync();
    // transactionId da resposta da Integration (não é segredo). A credencial não é registrada.
    using (var doc = System.Text.Json.JsonDocument.Parse(json))
    {
        Guid? tx = doc.RootElement.TryGetProperty("transactionId", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String && t.TryGetGuid(out var g) ? g : null;
        string? order = doc.RootElement.TryGetProperty("orderId", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String ? o.GetString() : null;
        Chk3Trace.Mark(ctx, passo, tx, passo == "6" ? "valida" : "credencial_digitacao_emitida", orderId: order);
    }
    return Results.Content(json, "application/json", Encoding.UTF8, 200);
}

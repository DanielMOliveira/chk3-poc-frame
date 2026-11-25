using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Chk3.Shared;

// Web App Cartão (endpoint pay, CDE). Iframe filho da Tela de opções. Não loga nem persiste PAN.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });

var internalKey = Env.Required("INTERNAL_API_KEY");
var opcoesOrigin = Env.Required("OPCOES_PUBLIC_ORIGIN").TrimEnd('/');
var canalOrigin = Env.Required("CANAL_PUBLIC_ORIGIN").TrimEnd('/');
builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(Env.Required("INTEGRATION_INTERNAL_URL")); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromSeconds(5); });
// Timeout maior que o do Gateway na API Autorização, para receber o "indeterminado" dela.
builder.Services.AddHttpClient("autorizacao", c => { c.BaseAddress = new Uri(Env.Required("AUTORIZACAO_URL")); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromSeconds(20); });

var app = builder.Build();

// frame-ancestors verifica TODOS os ancestrais: Tela de opções (pai) e canal (avô).
app.UseSecurityHeaders($"default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors {opcoesOrigin} {canalOrigin}");
app.UseRejectQueryString();
app.UseStaticFiles();

// Card testing (v7 §3.4): limite de falhas de validação (9a) por credencial de digitação. Memória local (POC, 1 réplica).
var failures = new ConcurrentDictionary<string, int>();
const int MaxFailures = 5;
static string Key(string cred) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cred)));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- Passo 8: página de digitação, credencial de digitação por POST ----
app.MapPost("/digitacao", async (HttpContext ctx, IHttpClientFactory http) =>
{
    if (!ctx.Request.HasFormContentType) return Results.BadRequest();
    var form = await ctx.Request.ReadFormAsync();
    string tc = form["tc"].ToString();
    int.TryParse(form["installments"], out var installments);
    if (tc.Length is 0 or > 128) return Html(ErrorPage(), 401);

    using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/typing-credential/validate");
    req.Headers.Add(Web.TypingCredentialHeader, tc);
    using var resp = await http.CreateClient("integration").SendAsync(req);
    if (!resp.IsSuccessStatusCode) return Html(ErrorPage(), 401);
    var v = (await resp.Content.ReadFromJsonAsync<TypingValidation>())!;

    var e = HtmlEncoder.Default;
    // Credencial e snapshot vão em <meta> lidos pelo JS e removidos do DOM. Página no-store, sem log.
    return Html($$"""
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="chk3-tc" content="{{e.Encode(tc)}}">
        <meta name="chk3-snapshot" content="{{e.Encode(v.Snapshot)}}">
        <meta name="chk3-installments" content="{{(v.FixedInstallments ?? Math.Max(1, installments))}}">
        <meta name="chk3-parent-origin" content="{{e.Encode(opcoesOrigin)}}">
        <title>Dados do cartão</title><link rel="stylesheet" href="/digitacao.css">
        <script src="/antifraude-fake.js"></script>
        <script src="/digitacao.js" defer></script></head>
        <body>
        <form id="f" autocomplete="on" novalidate>
          <label>Número do cartão <input id="pan" inputmode="numeric" autocomplete="cc-number" maxlength="23" required></label>
          <div class="row">
            <label>Validade (MM/AA) <input id="exp" inputmode="numeric" autocomplete="cc-exp" maxlength="5" placeholder="MM/AA" required></label>
            <label>CVV <input id="cvv" inputmode="numeric" autocomplete="cc-csc" maxlength="3" required></label>
          </div>
          <p id="info"></p>
          <button id="pay" type="submit">Pagar</button>
          <p id="msg" role="status"></p>
        </form>
        </body></html>
        """, 200);
});

// ---- Passo 9: cartão + credencial de digitação no header -> API Autorização ----
app.MapPost("/api/authorize", async (HttpContext ctx, IHttpClientFactory http) =>
{
    string tc = ctx.Request.Headers[Web.TypingCredentialHeader].ToString();
    if (tc.Length is 0 or > 128) return Results.Json(new { status = "SessaoInvalida" }, statusCode: 401);
    var k = Key(tc);
    if (failures.TryGetValue(k, out var f) && f >= MaxFailures)
        return Results.Json(new { status = "LimiteDeTentativas" }, statusCode: 429);

    using var body = new StreamContent(ctx.Request.Body);
    body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    using var req = new HttpRequestMessage(HttpMethod.Post, "/authorize") { Content = body };
    req.Headers.Add(Web.TypingCredentialHeader, tc);
    using var resp = await http.CreateClient("autorizacao").SendAsync(req);
    var json = await resp.Content.ReadAsStringAsync();
    if ((int)resp.StatusCode == 422 && json.Contains("ValidacaoFalhou")) failures.AddOrUpdate(k, 1, (_, n) => n + 1);
    return Results.Content(json, "application/json", Encoding.UTF8, (int)resp.StatusCode);
});

app.Run();

static IResult Html(string html, int status) => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
static string ErrorPage() => """
    <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><title>Sessão inválida</title><link rel="stylesheet" href="/digitacao.css"></head>
    <body><p>Não foi possível abrir a digitação. Volte e tente de novo.</p></body></html>
    """;

record TypingValidation(Guid TransactionId, string Snapshot, int? FixedInstallments);

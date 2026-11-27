using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Chk3.Shared;

// Web App Cartão (endpoint pay, CDE). Iframe filho da Tela de opções. Não loga nem persiste PAN.
var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("cartao");
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });

var internalKey = Env.Required("INTERNAL_API_KEY");
var opcoesOrigin = Env.Required("OPCOES_PUBLIC_ORIGIN").TrimEnd('/');
var canalOrigin = Env.Required("CANAL_PUBLIC_ORIGIN").TrimEnd('/');
builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(Env.Required("INTEGRATION_INTERNAL_URL")); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromSeconds(5); });
// Timeout maior que o do Gateway na API Autorização, para receber o "indeterminado" dela.
builder.Services.AddHttpClient("autorizacao", c => { c.BaseAddress = new Uri(Env.Required("AUTORIZACAO_URL")); c.DefaultRequestHeaders.Add(Web.InternalKeyHeader, internalKey); c.Timeout = TimeSpan.FromMilliseconds(Math.Max(20_000, int.Parse(Env.Optional("GATEWAY_REAL_TIMEOUT_MS", "15000")) + 5_000)); });

var app = builder.Build();
app.UseChk3Trace();

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
    // Correlation id vindo da Tela de opções (transactionId). Só para rastreio; validado como GUID.
    if (Guid.TryParse(form["cid"].ToString(), out var cid)) Chk3Trace.SetCorrelation(ctx, cid);
    if (tc.Length is 0 or > 128) { Chk3Trace.Mark(ctx, "8", null, "sem_credencial"); return Html(ErrorPage(opcoesOrigin), 401); }

    using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/typing-credential/validate");
    req.Headers.Add(Web.TypingCredentialHeader, tc);
    using var resp = await http.CreateClient("integration").SendAsync(req);
    if (!resp.IsSuccessStatusCode) { Chk3Trace.Mark(ctx, "8", null, "recusada_integration_" + (int)resp.StatusCode); return Html(ErrorPage(opcoesOrigin), 401); }
    var v = (await resp.Content.ReadFromJsonAsync<TypingValidation>())!;
    Chk3Trace.Mark(ctx, "8", v.TransactionId, "pagina_digitacao_entregue");

    var e = HtmlEncoder.Default;
    // Parcelas 1..máximo da sessão, divisão padrão do cartão (floor; resto na 1ª), sem juros. O passo 10 revalida.
    var options = string.Concat(Enumerable.Range(1, v.MaxInstallments).Select(n =>
        $"<option value=\"{n}\">{e.Encode(Installments.Label(v.AmountCents, n))}</option>"));
    string M(string name, object? value) => $"<meta name=\"chk3-{name}\" content=\"{e.Encode(value?.ToString() ?? "")}\">";
    // Credencial e snapshot vão em <meta> lidos pelo JS e removidos do DOM. Página no-store, sem log.
    // Valor só para exibição (botão e parcelas); o passo 9 não envia valor.
    return Html($$"""
        <!doctype html><html lang="pt-BR"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        {{M("tc", tc)}}
        {{M("snapshot", v.Snapshot)}}
        {{M("parent-origin", opcoesOrigin)}}
        {{M("amount", v.AmountCents)}}
        {{M("currency", v.Currency)}}
        {{M("brands", string.Join(",", v.Brands ?? []))}}
        {{M("tx", v.TransactionId)}}
        <title>Dados do cartão</title>
        <link rel="stylesheet" href="/mp-base.css"><link rel="stylesheet" href="/digitacao.css">
        <script src="/antifraude-fake.js"></script>
        <script src="/digitacao.js" defer></script></head>
        <body>
        <form id="f" autocomplete="on" novalidate>
          <div class="fieldwrap field">
            <label for="pan">Número do cartão</label>
            <input class="input card" id="pan" inputmode="numeric" autocomplete="cc-number" placeholder="0000 0000 0000 0000" maxlength="23" required>
            <span class="cardbrand" id="brand"></span>
          </div>
          <div class="field">
            <label for="name">Nome impresso no cartão</label>
            <input class="input" id="name" autocomplete="cc-name" placeholder="Como está no cartão" maxlength="60">
          </div>
          <div class="row2">
            <div class="field">
              <label for="exp">Validade</label>
              <input class="input" id="exp" inputmode="numeric" autocomplete="cc-exp" placeholder="MM/AA" maxlength="7" required>
            </div>
            <div class="field">
              <label for="cvv">CVV</label>
              <input class="input" id="cvv" inputmode="numeric" autocomplete="cc-csc" placeholder="123" maxlength="4" required>
            </div>
          </div>
          <div class="field">
            <label for="inst">Parcelamento</label>
            <select id="inst">{{options}}</select>
          </div>
          <div class="note-erro" id="msg" role="alert" hidden></div>
          <button class="btn" id="pay" type="submit" disabled>Pagar</button>
          <div class="foot-secure">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="4" y="11" width="16" height="9" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>
            Não armazenamos o número nem o CVV
          </div>
        </form>
        </body></html>
        """, 200);
});

// ---- Passo 9: cartão + credencial de digitação no header -> API Autorização ----
app.MapPost("/api/authorize", async (HttpContext ctx, IHttpClientFactory http) =>
{
    string tc = ctx.Request.Headers[Web.TypingCredentialHeader].ToString();
    if (tc.Length is 0 or > 128) { Chk3Trace.Mark(ctx, "9", null, "sem_credencial"); return Results.Json(new { status = "SessaoInvalida" }, statusCode: 401); }
    var k = Key(tc);
    if (failures.TryGetValue(k, out var f) && f >= MaxFailures)
    {
        Chk3Trace.Mark(ctx, "9", Guid.TryParse(Chk3Trace.Current, out var c) ? c : null, "9a_limite_de_tentativas");
        return Results.Json(new { status = "LimiteDeTentativas" }, statusCode: 429);
    }

    using var body = new StreamContent(ctx.Request.Body);
    body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    using var req = new HttpRequestMessage(HttpMethod.Post, "/authorize") { Content = body };
    req.Headers.Add(Web.TypingCredentialHeader, tc);
    using var resp = await http.CreateClient("autorizacao").SendAsync(req);
    var json = await resp.Content.ReadAsStringAsync();
    if ((int)resp.StatusCode == 422 && json.Contains("ValidacaoFalhou")) failures.AddOrUpdate(k, 1, (_, n) => n + 1);
    // Resultado devolvido ao navegador (vira postMessage no passo 14). Só status e transactionId.
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        Guid? tx = r.TryGetProperty("transactionId", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String && t.TryGetGuid(out var g) ? g : null;
        var st = r.TryGetProperty("status", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() : null;
        var erro = r.TryGetProperty("erro", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
        Chk3Trace.Mark(ctx, "9-14", tx, erro is null ? st ?? "?" : $"{st}_{erro}");
    }
    catch (System.Text.Json.JsonException) { Chk3Trace.Mark(ctx, "9-14", null, "resposta_invalida"); }
    return Results.Content(json, "application/json", Encoding.UTF8, (int)resp.StatusCode);
});

app.Run();

static IResult Html(string html, int status) => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
// Credencial inválida/expirada: avisa a Tela de opções (que mostra o erro no layout do modelo).
static string ErrorPage(string parentOrigin) => $"""
    <!doctype html><html lang="pt-BR"><head><meta charset="utf-8">
    <meta name="chk3-parent-origin" content="{HtmlEncoder.Default.Encode(parentOrigin)}">
    <title>Sessão inválida</title><link rel="stylesheet" href="/mp-base.css"><link rel="stylesheet" href="/digitacao.css">
    <script src="/digitacao-erro.js" defer></script></head>
    <body><p class="note-erro">Não foi possível abrir a digitação. Volte à loja e tente de novo.</p></body></html>
    """;

record TypingValidation(Guid TransactionId, string Snapshot, int MaxInstallments, string[]? Brands, long AmountCents, string Currency);

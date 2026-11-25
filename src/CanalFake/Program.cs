using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Chk3.Shared;

// Canal fake: simula o "Canal Workflow". Chama POST /sessions do servidor (token do canal nunca vai
// ao navegador) e embute o iframe da Tela de opções.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var jwtSecret = Env.Secret("CHANNEL_JWT_SECRET");
var jwtIssuer = Env.Required("CHANNEL_JWT_ISSUER");
var channelId = Env.Optional("CANAL_ID", "canal-fake");
var opcoesOrigin = Env.Required("OPCOES_PUBLIC_ORIGIN").TrimEnd('/');
builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(Env.Required("INTEGRATION_PUBLIC_URL")); c.Timeout = TimeSpan.FromSeconds(10); });

var app = builder.Build();
app.UseSecurityHeaders($"default-src 'none'; script-src 'self'; style-src 'self'; frame-src {opcoesOrigin}; form-action 'self'; base-uri 'none'; frame-ancestors 'none'");
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/", () => Html("""
    <h1>Canal fake (POC CHK3)</h1>
    <form method="post" action="/checkout">
      <label>Pedido <input name="orderId" value="PED-1001" required></label>
      <label>Valor em centavos <input name="amountCents" value="1000" required></label>
      <label>Parcelas fixas (opcional) <input name="installments" value=""></label>
      <button>Pagar</button>
    </form>
    <p>Cartões de teste: 4111111111111111 (aprova), 4000000000000002 (recusa), 4000000000000119 (timeout = indeterminado). Validade futura, CVV 123.</p>
    """));

// Passos 1 a 4 do lado do canal. reopen=1 reusa a mesma Idempotency-Key (3b).
app.MapPost("/checkout", async (HttpContext ctx, IHttpClientFactory http) =>
{
    var f = await ctx.Request.ReadFormAsync();
    var idemKey = f["idemKey"].ToString() is { Length: > 0 } k ? k : "canal-" + Guid.NewGuid().ToString("N");
    var body = new Dictionary<string, object?>
    {
        ["orderId"] = f["orderId"].ToString(),
        ["amountCents"] = long.TryParse(f["amountCents"], out var a) ? a : 0,
        ["currency"] = "BRL",
        ["installments"] = int.TryParse(f["installments"], out var n) ? n : null,
        ["camposAdicionais"] = new Dictionary<string, string> { ["origem"] = "canal-fake" },
    };
    using var req = new HttpRequestMessage(HttpMethod.Post, "/sessions") { Content = JsonContent.Create(body) };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintJwt());
    req.Headers.Add("Idempotency-Key", idemKey);
    using var resp = await http.CreateClient("integration").SendAsync(req);
    var json = await resp.Content.ReadAsStringAsync();
    var e = HtmlEncoder.Default;
    if (!resp.IsSuccessStatusCode)
        return Html($"<h1>Erro {(int)resp.StatusCode}</h1><pre>{e.Encode(json)}</pre><a href=\"/\">Voltar</a>");

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var frameUrl = root.GetProperty("frameUrl").GetString();
    var hidden = $"""
        <input type="hidden" name="idemKey" value="{e.Encode(idemKey)}">
        <input type="hidden" name="orderId" value="{e.Encode(f["orderId"].ToString())}">
        <input type="hidden" name="amountCents" value="{e.Encode(f["amountCents"].ToString())}">
        <input type="hidden" name="installments" value="{e.Encode(f["installments"].ToString())}">
        """;
    var frame = frameUrl is null
        ? $"<p>Sessão sem frame novo. Estado: <b>{e.Encode(root.GetProperty("state").GetString() ?? "")}</b></p>"
        : $"<iframe id=\"frame\" src=\"{e.Encode(frameUrl)}\" allow=\"payment\" title=\"Pagamento\"></iframe>";
    return Html($"""
        <h1>Canal fake: checkout</h1>
        <p>transactionId: <code>{e.Encode(root.GetProperty("transactionId").ToString())}</code> (HTTP {(int)resp.StatusCode})</p>
        {frame}
        <p id="evento" role="status"></p>
        <div class="acoes">
          <button id="recarregar" type="button">Recarregar frame (mesma credencial)</button>
          <form method="post" action="/checkout">{hidden}<button>Reabrir com a mesma Idempotency-Key (3b)</button></form>
          <a href="/">Novo pedido</a>
        </div>
        <script src="/canal.js" data-opcoes-origin="{e.Encode(opcoesOrigin)}"></script>
        """);
});

app.Run();

string MintJwt()
{
    static string B64(byte[] b) => SnapshotSigner.Base64Url(b);
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
    var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = jwtIssuer, aud = "chk3-integration", sub = channelId, scope = "sessions:create", iat = now, exp = now + 300 }));
    var sig = B64(HMACSHA256.HashData(jwtSecret, Encoding.ASCII.GetBytes(header + "." + payload)));
    return $"{header}.{payload}.{sig}";
}

static IResult Html(string inner) => Results.Content(
    $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>Canal fake</title><link rel=\"stylesheet\" href=\"/canal.css\"></head><body>{inner}</body></html>",
    "text/html; charset=utf-8", Encoding.UTF8);

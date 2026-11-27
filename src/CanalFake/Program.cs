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
builder.AddChk3Logging("canal");
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var jwtSecret = Env.Secret("CHANNEL_JWT_SECRET");
var jwtIssuer = Env.Required("CHANNEL_JWT_ISSUER");
var channelId = Env.Optional("CANAL_ID", "canal-fake");
var opcoesOrigin = Env.Required("OPCOES_PUBLIC_ORIGIN").TrimEnd('/');
builder.Services.AddHttpClient("integration", c => { c.BaseAddress = new Uri(Env.Required("INTEGRATION_PUBLIC_URL")); c.Timeout = TimeSpan.FromSeconds(10); });

var app = builder.Build();
app.UseChk3Trace(); // gera o X-Correlation-Id antes do POST /sessions
app.UseSecurityHeaders($"default-src 'none'; script-src 'self'; style-src 'self'; frame-src {opcoesOrigin}; form-action 'self'; base-uri 'none'; frame-ancestors 'none'");
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Campos do formulário -> JSON do POST /sessions. Só "amount" é obrigatório (ata 26/11); campo vazio não vai no JSON.
string[] billingFields = ["customerIdentity", "name", "email", "phone", "address", "address2", "city", "state", "postalCode", "country"];
var billingLabels = new Dictionary<string, string>
{
    ["customerIdentity"] = "Documento (CPF/CNPJ)", ["name"] = "Nome do comprador", ["email"] = "E-mail", ["phone"] = "Telefone (só dígitos)",
    ["address"] = "Endereço", ["address2"] = "Complemento", ["city"] = "Cidade", ["state"] = "UF", ["postalCode"] = "CEP (8 dígitos)", ["country"] = "País (BR)",
};

app.MapGet("/", () =>
{
    string F(string id, string label, string value = "", string extra = "") =>
        $"<div class=\"field\"><label for=\"{id}\">{label}</label><input class=\"input\" id=\"{id}\" name=\"{id}\" value=\"{value}\" {extra}></div>";
    var billing = string.Concat(billingFields.Select(f => F("b_" + f, billingLabels[f])));
    return Html($"""
    <main class="store">
      <header class="store-top"><span class="logo">LE</span><div><b>Loja Exemplo</b><small>Canal fake · POC CHK3</small></div></header>
      <form method="post" action="/checkout" class="store-card">
        <div class="h1">Seu pedido</div>
        <div class="sub">O canal cria a sessão no servidor (POST /sessions em JSON, com o token do canal) e abre o frame de pagamento. Só o valor é obrigatório.</div>
        {F("amount", "Valor total em centavos (obrigatório)", "24990", "inputmode=\"numeric\" required")}
        <div class="row2">
          {F("orderId", "Pedido (opcional; sem ele é gerado)", "PED-1001")}
          {F("installments", "Parcelas máximas (opcional; padrão do canal)", "", "inputmode=\"numeric\"")}
        </div>
        <details class="opt"><summary>Campos opcionais: comprador, retorno e descrição na fatura</summary>
          <div class="grid2">{billing}</div>
          {F("urlReturn", "URL de retorno (opcional)")}
          {F("softDescriptor", "Descrição na fatura, até 13 (opcional)", "", "maxlength=\"13\"")}
        </details>
        <button class="btn" id="pagar">Ir para o pagamento</button>
        <div class="note-info">Cartões de teste: 4111 1111 1111 1111 (aprova), 4000 0000 0000 0002 (recusa), 4000 0000 0000 0119 (timeout, indeterminado). Validade futura, CVV 123.</div>
      </form>
    </main>
    """);
});

// Passos 1 a 4 do lado do canal. Reabrir (3b) reenvia o MESMO JSON com a mesma Idempotency-Key.
app.MapPost("/checkout", async (HttpContext ctx, IHttpClientFactory http) =>
{
    var f = await ctx.Request.ReadFormAsync();
    var idemKey = f["idemKey"].ToString() is { Length: > 0 } k ? k : "canal-" + Guid.NewGuid().ToString("N");
    var e = HtmlEncoder.Default;
    string bodyJson;
    if (f["body"].ToString() is { Length: > 0 and <= 8192 } reopen) bodyJson = reopen;
    else
    {
        string? V(string n) => f[n].ToString().Trim() is { Length: > 0 } v ? v : null;
        var body = new Dictionary<string, object?>();
        body["amount"] = long.TryParse(V("amount"), out var a) ? a : V("amount"); // inválido segue como veio: a Integration recusa
        if (V("orderId") is { } o) body["orderId"] = o;
        if (V("installments") is { } inst) body["installments"] = int.TryParse(inst, out var n) ? n : inst;
        var bill = billingFields.Where(b => V("b_" + b) is not null).ToDictionary(b => b, b => V("b_" + b));
        if (bill.Count > 0) body["billing"] = bill;
        if (V("urlReturn") is { } u) body["urlReturn"] = u;
        if (V("softDescriptor") is { } sd) body["softDescriptor"] = sd;
        body["camposAdicionais"] = new Dictionary<string, string> { ["origem"] = "canal-fake" };
        bodyJson = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true });
    }
    using var req = new HttpRequestMessage(HttpMethod.Post, "/sessions") { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintJwt());
    req.Headers.Add("Idempotency-Key", idemKey);
    using var resp = await http.CreateClient("integration").SendAsync(req);
    var json = await resp.Content.ReadAsStringAsync();
    // JSON enviado visível no demo (sem o token do canal). Nunca vai para o log.
    var sent = $"<details class=\"opt\"><summary>JSON enviado no POST /sessions</summary><pre class=\"json\">{e.Encode(bodyJson)}</pre></details>";
    if (!resp.IsSuccessStatusCode)
    {
        Chk3Trace.Mark(ctx, "1-4", null, "erro_integration_" + (int)resp.StatusCode); // orderId recusado não vai para o log
        return Html($"<main class=\"store\"><div class=\"store-card\"><div class=\"h1\">Erro {(int)resp.StatusCode} ao criar a sessão</div><pre class=\"note-erro\">{e.Encode(json)}</pre>{sent}<a class=\"link\" href=\"/\">Voltar</a></div></main>");
    }

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var frameUrl = root.GetProperty("frameUrl").GetString();
    Guid? txId = root.TryGetProperty("transactionId", out var txEl) && txEl.ValueKind == JsonValueKind.String && txEl.TryGetGuid(out var g) ? g : null;
    var orderId = root.TryGetProperty("orderId", out var oEl) && oEl.ValueKind == JsonValueKind.String ? oEl.GetString() : null;
    // A frameUrl (com #pc=) nunca vai para o log.
    Chk3Trace.Mark(ctx, "1-4", txId, $"sessao_{root.GetProperty("state").GetString()}_{(frameUrl is null ? "sem_frame" : "frame_embutido")}_http{(int)resp.StatusCode}", orderId: orderId);
    var hidden = $"""
        <input type="hidden" name="idemKey" value="{e.Encode(idemKey)}">
        <input type="hidden" name="body" value="{e.Encode(bodyJson)}">
        """;
    var frame = frameUrl is null
        ? $"<div class=\"store-card\"><div class=\"h1\">Sessão sem frame novo</div><div class=\"sub\">Estado atual: <b>{e.Encode(root.GetProperty("state").GetString() ?? "")}</b></div></div>"
        : $"<iframe id=\"frame\" src=\"{e.Encode(frameUrl)}\" allow=\"payment\" title=\"Pagamento\"></iframe>";
    return Html($"""
        <main class="checkout">
          <header class="store-top"><span class="logo">LE</span><div><b>Loja Exemplo</b><small>Canal fake · POC CHK3</small></div></header>
          {frame}
          <aside class="store-card side">
            <div class="kv"><span>transactionId</span><code>{e.Encode(root.GetProperty("transactionId").ToString())}</code></div>
            <div class="kv"><span>Pedido</span><code>{e.Encode(orderId ?? "")}</code></div>
            <div class="kv"><span>POST /sessions</span><code>HTTP {(int)resp.StatusCode}</code></div>
            {sent}
            <p id="evento" class="evento" role="status">Aguardando o frame…</p>
            <button id="recarregar" class="btn ghost" type="button">Recarregar frame (mesma credencial)</button>
            <form method="post" action="/checkout">{hidden}<button class="btn ghost mt10">Reabrir com a mesma Idempotency-Key (3b)</button></form>
            <a class="link" href="/">Novo pedido</a>
          </aside>
        </main>
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
    $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Loja Exemplo · Canal fake</title><link rel=\"stylesheet\" href=\"/mp-base.css\"><link rel=\"stylesheet\" href=\"/canal.css\"></head><body class=\"bg-mp\">{inner}</body></html>",
    "text/html; charset=utf-8", Encoding.UTF8);

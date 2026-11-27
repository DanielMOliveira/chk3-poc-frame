using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Chk3.Shared;

// Mock do Gateway MCN (passos 11-12). Recebe o <transaction-request> em XML (formato do Daniel, 26/11) e valida
// estrutura, formatos e merchant. A RESPOSTA (<transaction-response> com status/code) é INVENTADA para o POC.
// TODO(v7 §4): contrato real (códigos de recusa, timeout, deviceSessionId, dado de cartão na resposta 12).
// NUNCA loga o XML, PAN, CVV, validade, nome ou merchantKey: só o nome do campo que falhou.
var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("gateway-mock");
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16 * 1024; });
var app = builder.Build();
app.UseChk3Trace();

var apiKey = Env.Required("GATEWAY_API_KEY");
var merchantId = Env.Required("GATEWAY_MERCHANT_ID");
var merchantKey = Env.Required("GATEWAY_MERCHANT_KEY");
var methodInstallments = Env.Optional("GATEWAY_METHOD_PARCELADO", "2");
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

app.MapPost("/poc/v1/authorize", async (HttpContext ctx) =>
{
    if (!Web.FixedTimeEquals(ctx.Request.Headers["X-Gateway-Key"], apiKey)) return Results.StatusCode(401);
    // O XML não traz o transactionId: ele vem do X-Correlation-Id enviado pela API Autorização.
    Guid? tx = Guid.TryParse(Chk3Trace.Current, out var g) ? g : null;
    if (ctx.Request.ContentType?.Split(';')[0].Trim() != "application/xml")
    {
        Chk3Trace.Mark(ctx, "11-12", tx, "content_type_invalido");
        return Results.StatusCode(415);
    }
    var doc = await SafeXml.TryLoadAsync(ctx.Request.Body, ctx.RequestAborted);
    var (erro, req) = TransactionRequest.Validate(doc, merchantId, merchantKey, methodInstallments);
    if (erro is not null)
    {
        Chk3Trace.Mark(ctx, "11-12", tx, "xml_invalido_" + erro, orderId: req?.Reference);
        return Xml(erro == "verification" ? 401 : 400, "error", "XML_INVALIDO", ("field", erro));
    }
    var r = req!;
    var ok = $"xml_ok_n{r.NumberOfPayments}_m{r.Method}";
    if (!testCards.TryGetValue(r.CardNumber, out var behavior))
    {
        Chk3Trace.Mark(ctx, "11-12", tx, ok + "_pan_nao_e_de_teste", orderId: r.Reference);
        return Xml(422, "error", "PAN_NAO_E_DE_TESTE");
    }

    switch (behavior)
    {
        case "timeout":
            try { await Task.Delay(timeoutDelay, ctx.RequestAborted); }
            catch (OperationCanceledException)
            {
                Chk3Trace.Mark(ctx, "11-12", tx, ok + "_timeout_simulado_cliente_desistiu", orderId: r.Reference); // 12b do lado do Gateway
                return Results.StatusCode(499);
            }
            Chk3Trace.Mark(ctx, "11-12", tx, ok + "_approved_00_atrasado", orderId: r.Reference);
            return Xml(200, "approved", "00"); // o cliente já desistiu (12b)
        case "decline":
            Chk3Trace.Mark(ctx, "11-12", tx, ok + "_declined_05", orderId: r.Reference);
            return Xml(200, "declined", "05");
        default:
            Chk3Trace.Mark(ctx, "11-12", tx, ok + "_approved_00", orderId: r.Reference);
            return Xml(200, "approved", "00", ("authorizationCode", "POC" + Random.Shared.Next(100000, 999999)));
    }
});

app.Run();

// Resposta inventada para o POC: <transaction-response><status/><code/>...</transaction-response>.
static IResult Xml(int status, string st, string code, params (string Name, string Value)[] extra) =>
    Results.Content(new XElement("transaction-response", new XElement("status", st), new XElement("code", code),
        extra.Select(e => new XElement(e.Name, e.Value))).ToString(SaveOptions.DisableFormatting),
        "application/xml", Encoding.UTF8, status);

sealed record TransactionRequest(string Reference, int NumberOfPayments, string Method, string CardNumber)
{
    static readonly HashSet<string> BillingFields = ["customerIdentity", "name", "address", "address2", "city", "state", "postalCode", "country", "phone", "email"];
    static readonly string[] PaymentOrder = ["acquirer", "method", "amount", "currency", "country", "numberOfPayments", "groupNumber", "flag",
        "cardHolder", "cardNumber", "cardSecurityCode", "cardExpirationDate", "saveCreditCard", "generateToken", "departureTax", "softDescriptor"];

    /// <summary>Valida o XML. Retorna o caminho do primeiro campo inválido (sem o valor) ou null.</summary>
    public static (string? Erro, TransactionRequest? Req) Validate(XDocument? doc, string merchantId, string merchantKey, string methodInstallments)
    {
        var root = doc?.Root;
        if (root?.Name.LocalName != "transaction-request") return ("transaction-request", null);
        if (Names(root) != "version,verification,authorize") return ("transaction-request_ordem", null);
        if (root.Element("version")!.Value != "1.0.0") return ("version", null);
        var ver = root.Element("verification")!;
        if (Names(ver) != "merchantId,merchantKey" || ver.Element("merchantId")!.Value != merchantId
            || !Web.FixedTimeEquals(ver.Element("merchantKey")!.Value, merchantKey)) return ("verification", null);

        var auth = root.Element("authorize")!;
        if (Names(auth) != "order,payment,billing,urlReturn,fraud,customField") return ("authorize_ordem", null);
        var order = auth.Element("order")!;
        if (Names(order) != "reference,totalAmount") return ("order", null);
        var reference = order.Element("reference")!.Value;
        if (!Regex.IsMatch(reference, "^[A-Za-z0-9._-]{1,64}$")) return ("order/reference", null);
        if (!long.TryParse(order.Element("totalAmount")!.Value, out var total) || total <= 0) return ("order/totalAmount", null);

        var p = auth.Element("payment")!;
        if (Names(p) != string.Join(',', PaymentOrder)) return ("payment_ordem", null);
        string V(string n) => p.Element(n)!.Value;
        string? fail = null;
        void Check(bool ok, string campo) { if (!ok) fail ??= "payment/" + campo; }
        Check(V("acquirer") is "1" or "20" or "22", "acquirer");
        int.TryParse(V("numberOfPayments"), out var n);
        Check(n is >= 1 and <= 24, "numberOfPayments");
        Check(n == 1 ? V("method") == "1" : V("method") == methodInstallments, "method");
        Check(V("amount") == total.ToString(), "amount");
        Check(V("currency") == "986", "currency");
        Check(V("country") == "BRA", "country");
        Check(V("groupNumber") == "0", "groupNumber");
        Check(V("flag") is "visa" or "mastercard" or "elo" or "amex", "flag");
        Check(Regex.IsMatch(V("cardHolder"), @"^[\p{L}\p{M} .'-]{2,60}$"), "cardHolder");
        Check(V("cardNumber").All(char.IsAsciiDigit) && Card.Luhn(V("cardNumber")), "cardNumber");
        Check(Regex.IsMatch(V("cardSecurityCode"), "^[0-9]{3,4}$"), "cardSecurityCode");
        var exp = V("cardExpirationDate");
        Check(Regex.IsMatch(exp, "^20[0-9]{2}(0[1-9]|1[0-2])$")
              && new DateTime(int.Parse(exp[..4]), int.Parse(exp[4..]), 1).AddMonths(1) > DateTime.UtcNow, "cardExpirationDate");
        Check(V("saveCreditCard") is "true" or "false", "saveCreditCard");
        Check(V("generateToken") is "true" or "false", "generateToken");
        Check(V("departureTax") == "0", "departureTax");
        Check(Regex.IsMatch(V("softDescriptor"), "^[A-Za-z0-9 ]{1,13}$"), "softDescriptor");
        if (fail is not null) return (fail, new(reference, n, V("method"), ""));

        // billing: só campos conhecidos, sem vazio (dado não informado = elemento omitido).
        var billing = auth.Element("billing")!;
        if (billing.Elements().Any(e => !BillingFields.Contains(e.Name.LocalName) || e.Value.Trim().Length == 0)) return ("billing", new(reference, n, V("method"), ""));
        var url = auth.Element("urlReturn")!.Value;
        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _)) return ("urlReturn", new(reference, n, V("method"), ""));
        if (auth.Element("fraud")!.Value is not ("true" or "false")) return ("fraud", new(reference, n, V("method"), ""));
        if (auth.Element("customField")!.HasElements || auth.Element("customField")!.Value.Length > 0) return ("customField", new(reference, n, V("method"), ""));
        return (null, new(reference, n, V("method"), V("cardNumber")));
    }

    static string Names(XElement e) => string.Join(',', e.Elements().Select(x => x.Name.LocalName));
}

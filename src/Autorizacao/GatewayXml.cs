using System.Xml.Linq;
using Chk3.Shared;

namespace Chk3.Autorizacao;

/// <summary>Configuração do merchant no Gateway (só do .env; a merchantKey nunca vai para código, log ou commit).</summary>
public sealed record GatewayMerchant(string MerchantId, string MerchantKey, string Acquirer, string SoftDescriptor,
    string MethodInstallments, bool SaveCreditCard, string? Environment = null);

/// <summary>Dados do passo 10c (Integration, sem PAN) e do passo 9 (navegador, com PAN) para o passo 11.</summary>
public sealed record GatewayOrder(string OrderId, long AmountCents, string Currency, int Installments,
    Dictionary<string, string>? Billing, string? UrlReturn, string? SoftDescriptor);

public sealed record GatewayCard(string Number, string HolderName, string SecurityCode, int ExpMonth, int ExpYear, string Flag);

/// <summary>
/// Monta o &lt;transaction-request&gt; do Gateway (formato do Daniel, 26/11). Contém PAN, CVV e merchantKey:
/// o XML existe só em memória durante o passo 11 e NUNCA é logado.
/// </summary>
public static class GatewayXml
{
    // Ordem dos elementos do <billing> no modelo.
    static readonly string[] BillingOrder = ["customerIdentity", "name", "address", "address2", "city", "state", "postalCode", "country", "phone", "email"];

    public static string Build(GatewayMerchant m, GatewayOrder o, GatewayCard c)
    {
        // 1 = crédito à vista (modelo). Parcelado: valor do .env, PENDENTE de confirmação com o Gateway.
        var method = o.Installments == 1 ? "1" : m.MethodInstallments;
        var doc = new XElement("transaction-request",
            new XElement("version", "1.0.0"),
            new XElement("verification",
                new XElement("merchantId", m.MerchantId),
                new XElement("merchantKey", m.MerchantKey),
                // <environment> logo depois da merchantKey (pedido do Daniel, 27/11). GATEWAY_ENVIRONMENT vazio = omitido.
                string.IsNullOrEmpty(m.Environment) ? null : new XElement("environment", m.Environment)),
            new XElement("authorize",
                new XElement("order",
                    new XElement("reference", o.OrderId),
                    new XElement("totalAmount", o.AmountCents)),
                new XElement("payment",
                    new XElement("acquirer", m.Acquirer),
                    new XElement("method", method),
                    new XElement("amount", o.AmountCents),
                    new XElement("currency", o.Currency == "BRL" ? "986" : throw new InvalidOperationException("moeda")),
                    new XElement("country", "BRA"),
                    new XElement("numberOfPayments", o.Installments),
                    new XElement("groupNumber", 0),
                    new XElement("flag", c.Flag),
                    new XElement("cardHolder", c.HolderName),
                    new XElement("cardNumber", c.Number),
                    new XElement("cardSecurityCode", c.SecurityCode),
                    new XElement("cardExpirationDate", $"{c.ExpYear:0000}{c.ExpMonth:00}"),
                    new XElement("saveCreditCard", m.SaveCreditCard ? "true" : "false"),
                    new XElement("generateToken", "false"),
                    new XElement("departureTax", 0),
                    new XElement("softDescriptor", o.SoftDescriptor ?? m.SoftDescriptor)),
                Billing(o.Billing),
                new XElement("urlReturn", o.UrlReturn ?? ""),
                new XElement("fraud", "false"),
                new XElement("customField")));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    // Só os dados informados. País BR por padrão quando há algum dado do comprador. Sem dado: <billing />.
    static XElement Billing(Dictionary<string, string>? b)
    {
        var el = new XElement("billing");
        if (b is null || b.Count == 0) return el;
        foreach (var k in BillingOrder)
        {
            var v = b.GetValueOrDefault(k) ?? (k == "country" ? "BR" : null);
            if (!string.IsNullOrEmpty(v)) el.Add(new XElement(k, v));
        }
        return el;
    }

    /// <summary>
    /// Lê a resposta. Dois formatos:
    /// - mock (inventado no POC): &lt;transaction-response&gt;&lt;status&gt;approved|declined&lt;/status&gt;&lt;code/&gt;;
    /// - Gateway real (observado em 27/11, já desembrulhado de &lt;string&gt;): &lt;transaction-response id versao&gt;&lt;transactionId/&gt;
    ///   &lt;status&gt;&lt;code/&gt;&lt;message/&gt;&lt;/status&gt;... O significado dos códigos NÃO foi informado: aprovado/recusado só pelas
    ///   listas do .env (GATEWAY_APPROVED_CODES / GATEWAY_DECLINED_CODES); fora delas, Indeterminada.
    /// Status = approved | declined | null (indeterminado). Code = código de retorno. GatewayRef = transactionId do Gateway.
    /// </summary>
    public static (string? Status, string? Code, string? GatewayRef) ParseResponse(XDocument? d,
        IReadOnlyCollection<string>? approvedCodes = null, IReadOnlyCollection<string>? declinedCodes = null)
    {
        var r = SafeXml.Unwrap(d)?.Root;
        if (r?.Name.LocalName != "transaction-response") return (null, null, null);
        var st = r.Element("status");
        if (st is null) return (null, null, null);
        if (!st.HasElements && st.Value is "approved" or "declined") return (st.Value, r.Element("code")?.Value, null); // mock
        // Gateway real: processada = <status>N</status><message/> (ex.: 4 UNAUTHORIZED); erro = <status><code/><message/></status>.
        var code = Clean(st.HasElements ? st.Element("code")?.Value : st.Value);
        var gwRef = Clean(r.Element("transactionId")?.Value, 64);
        var status = code is null ? null
            : approvedCodes?.Contains(code) == true ? "approved"
            : declinedCodes?.Contains(code) == true ? "declined" : null;
        return (status, code, gwRef);
    }

    /// <summary>Erro de negócio do Gateway real: result/error/code e details (texto fixo do Gateway, até 120 caracteres, sem dígitos longos).</summary>
    public static (string? ErrorCode, string? Details) ParseError(XDocument? d)
    {
        var e = SafeXml.Unwrap(d)?.Root?.Element("result")?.Element("error");
        if (e is null) return (null, null);
        var det = e.Element("details")?.Value;
        if (det is not null)
        {
            det = new string(det.Where(c => !char.IsControl(c)).ToArray()).Trim();
            det = System.Text.RegularExpressions.Regex.Replace(det, "[0-9]{6,}", "***");
            if (det.Length > 120) det = det[..120];
            if (det.Length == 0) det = null;
        }
        return (Clean(e.Element("code")?.Value), det);
    }

    // Código/ref só com [A-Za-z0-9_-] (vai para log e para a Integration).
    static string? Clean(string? v, int max = 32) => v?.Trim() is { Length: > 0 } t && t.Length <= max && t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? t : null;

    /// <summary>Retorno do adquirente na resposta processada: processors/processor/responseCode e acquirerId.</summary>
    public static (string? ResponseCode, string? AcquirerId) ParseProcessor(XDocument? d)
    {
        var p = SafeXml.Unwrap(d)?.Root?.Element("processors")?.Element("processor");
        return (Clean(p?.Element("responseCode")?.Value), Clean(p?.Element("acquirerId")?.Value));
    }
}

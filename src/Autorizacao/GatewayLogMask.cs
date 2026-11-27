using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Chk3.Shared;

namespace Chk3.Autorizacao;

/// <summary>
/// Máscara do XML (ou JSON/texto) trocado com o Gateway, para log/Grafana.
/// cardNumber -> BIN6 + * + últimos 4; CVV, validade, portador, merchantId/merchantKey e dados pessoais do billing -> ***.
/// Em qualquer outro valor, sequência de 12+ dígitos (PAN, documento) vira BIN6/***/últimos 4.
/// Vale para a resposta de formato desconhecido também (estrutura mantida, valores sensíveis mascarados).
/// </summary>
public static partial class GatewayLogMask
{
    const int MaxLog = 6000;

    // Nomes de campo (minúsculos, sem '-'/'_') cujo valor nunca aparece.
    static readonly HashSet<string> Hidden =
    [
        "cardsecuritycode", "securitycode", "cvv", "cvc", "cvv2", "cardexpirationdate", "expirationdate", "expdate", "expiry",
        "expmonth", "expyear", "cardholder", "holder", "holdername", "merchantkey", "merchantid", "key", "apikey", "password", "secret",
        "token", "cardtoken", "customeridentity", "document", "cpf", "cnpj", "name", "address", "address2", "street",
        "phone", "email", "postalcode", "zipcode", "birthdate",
    ];
    static readonly HashSet<string> CardNumber = ["cardnumber", "pan", "number", "cardnum", "creditcardnumber"];

    [GeneratedRegex("[0-9]{12,19}")] private static partial Regex LongDigits();

    static string Norm(string n) => n.Replace("-", "").Replace("_", "").ToLowerInvariant();

    public static string MaskPan(string v)
    {
        var d = new string(v.Where(char.IsAsciiDigit).ToArray());
        return d.Length is >= 13 and <= 19 ? d[..6] + new string('*', d.Length - 10) + d[^4..] : "***";
    }

    static string MaskValue(string name, string value)
    {
        var n = Norm(name);
        if (value.Length == 0) return value;
        if (CardNumber.Contains(n)) return MaskPan(value);
        if (Hidden.Contains(n)) return "***";
        value = LongDigits().Replace(value, m => MaskPan(m.Value));
        return value.Length > 200 ? value[..200] + "…" : value;
    }

    /// <summary>Máscara de um corpo trocado com o Gateway. secrets: valores que nunca podem sair (ex.: merchantKey).</summary>
    public static string Mask(string? body, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(body)) return "";
        string masked;
        if (SafeXml.TryParse(body) is { Root: { } root })
        {
            foreach (var el in root.DescendantsAndSelf().Where(e => !e.HasElements)) el.Value = MaskValue(el.Name.LocalName, el.Value);
            foreach (var a in root.DescendantsAndSelf().SelectMany(e => e.Attributes())) a.Value = MaskValue(a.Name.LocalName, a.Value);
            masked = root.ToString(SaveOptions.DisableFormatting);
        }
        else if (TryJson(body) is { } node)
        {
            MaskJson(node, "");
            masked = node.ToJsonString();
        }
        else
        {
            // Formato desconhecido (HTML, texto): só a estrutura, sem dígitos longos.
            masked = "texto:" + LongDigits().Replace(body, m => MaskPan(m.Value));
        }
        foreach (var s in secrets) if (!string.IsNullOrEmpty(s)) masked = masked.Replace(s, "***");
        return masked.Length > MaxLog ? masked[..MaxLog] + "…(truncado)" : masked;
    }

    static JsonNode? TryJson(string s) { try { return JsonNode.Parse(s); } catch (JsonException) { return null; } }

    static void MaskJson(JsonNode node, string name)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    if (v is JsonValue jv) o[k] = MaskValue(k, jv.ToString());
                    else if (v is not null) MaskJson(v, k);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue jv) a[i] = MaskValue(name, jv.ToString());
                    else if (a[i] is { } c) MaskJson(c, name);
                }
                break;
        }
    }
}

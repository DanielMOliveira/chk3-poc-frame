using System.Xml;
using System.Xml.Linq;

namespace Chk3.Shared;

/// <summary>Leitura de XML sem DTD e sem resolver entidades externas (evita XXE e "billion laughs").</summary>
public static class SafeXml
{
    static XmlReaderSettings Settings(bool async) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = async,
        MaxCharactersInDocument = 64 * 1024, IgnoreComments = true,
    };

    public static async Task<XDocument?> TryLoadAsync(Stream s, CancellationToken ct = default)
    {
        try
        {
            using var r = XmlReader.Create(s, Settings(true));
            return await XDocument.LoadAsync(r, LoadOptions.None, ct);
        }
        catch (XmlException) { return null; }
    }

    /// <summary>
    /// O Gateway real (ASP.NET Web API) devolve o XML de negócio serializado como string:
    /// &lt;string xmlns="http://schemas.microsoft.com/2003/10/Serialization/"&gt;&amp;lt;?xml ...&amp;gt;&amp;lt;transaction-response ...&lt;/string&gt;.
    /// Aqui desembrulha (uma vez) e devolve o XML interno; senão, o próprio documento.
    /// </summary>
    public static XDocument? Unwrap(XDocument? doc) =>
        doc?.Root is { Name.LocalName: "string" } r && r.Name.NamespaceName == "http://schemas.microsoft.com/2003/10/Serialization/"
            && r.Value.TrimStart().StartsWith('<') ? TryParse(r.Value.Trim()) ?? doc : doc;

    public static XDocument? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var r = XmlReader.Create(new StringReader(text), Settings(false));
            return XDocument.Load(r, LoadOptions.None);
        }
        catch (XmlException) { return null; }
    }
}

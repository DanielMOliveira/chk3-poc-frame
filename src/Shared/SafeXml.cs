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

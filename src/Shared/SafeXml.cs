using System.Xml;
using System.Xml.Linq;

namespace Chk3.Shared;

/// <summary>Leitura de XML sem DTD e sem resolver entidades externas (evita XXE e "billion laughs").</summary>
public static class SafeXml
{
    public static async Task<XDocument?> TryLoadAsync(Stream s, CancellationToken ct = default)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, Async = true,
            MaxCharactersInDocument = 64 * 1024, IgnoreComments = true,
        };
        try
        {
            using var r = XmlReader.Create(s, settings);
            return await XDocument.LoadAsync(r, LoadOptions.None, ct);
        }
        catch (XmlException) { return null; }
    }
}

using System.Xml.Linq;
using Chk3.Autorizacao;

namespace Chk3.Tests;

/// <summary>O XML de ida/volta do Gateway só vai para o log/Grafana mascarado.</summary>
public class GatewayLogMaskTests
{
    const string Key = "CHAVE-SECRETA-DE-TESTE";

    [Fact]
    public void Requisicao_mascara_cartao_chave_e_dados_pessoais()
    {
        var billing = new Dictionary<string, string>
        {
            ["customerIdentity"] = "12345678909", ["name"] = "Jose da Silva", ["address"] = "Av. Paulista, 1000", ["address2"] = "10 Andar",
            ["city"] = "São Paulo", ["state"] = "SP", ["postalCode"] = "01310100", ["phone"] = "11999990000", ["email"] = "jose@exemplo.com",
        };
        var xml = GatewayXml.Build(new GatewayMerchant("chk3", Key, "22", "LOJA", "2", false),
            new GatewayOrder("PED-1", 1990, "BRL", 1, billing, "https://loja.exemplo/r", null),
            new GatewayCard("5453010000066167", "JOSE DA SILVA", "123", 5, 2030, "mastercard"));
        var m = GatewayLogMask.Mask(xml, Key);
        foreach (var s in new[] { "5453010000066167", "JOSE DA SILVA", "203005", Key, "12345678909", "Jose da Silva", "Paulista", "01310100", "11999990000", "jose@exemplo.com", "10 Andar" })
            Assert.DoesNotContain(s, m);
        var x = XElement.Parse(m);
        var p = x.Element("authorize")!.Element("payment")!;
        Assert.Equal("545301******6167", p.Element("cardNumber")!.Value);
        Assert.Equal("***", p.Element("cardSecurityCode")!.Value);
        Assert.Equal("***", p.Element("cardExpirationDate")!.Value);
        Assert.Equal("***", p.Element("cardHolder")!.Value);
        Assert.Equal("***", x.Element("verification")!.Element("merchantKey")!.Value);
        Assert.Equal("chk3", x.Element("verification")!.Element("merchantId")!.Value);
        Assert.Equal("22", p.Element("acquirer")!.Value);
        Assert.Equal("São Paulo", x.Element("authorize")!.Element("billing")!.Element("city")!.Value);
    }

    [Fact]
    public void Resposta_desconhecida_mantem_estrutura_e_mascara()
    {
        var xml = "<resp><status>X</status><card><number>5453010000066167</number><holder>FULANO</holder></card><msg>pan 4111111111111111</msg></resp>";
        var m = GatewayLogMask.Mask(xml);
        Assert.Contains("<status>X</status>", m);
        Assert.Contains("545301******6167", m);
        Assert.Contains("411111******1111", m);
        Assert.DoesNotContain("FULANO", m);

        var json = GatewayLogMask.Mask("""{"returnCode":"05","card":{"cardNumber":"5453010000066167","cvv":"123"},"email":"a@b.com"}""");
        Assert.Contains("\"returnCode\":\"05\"", json);
        Assert.DoesNotContain("5453010000066167", json);
        Assert.DoesNotContain("a@b.com", json);
        Assert.DoesNotContain("\"123\"", json);

        Assert.StartsWith("texto:", GatewayLogMask.Mask("Not Found 5453010000066167"));
        Assert.DoesNotContain("5453010000066167", GatewayLogMask.Mask("Not Found 5453010000066167"));
        Assert.Equal("", GatewayLogMask.Mask(""));
    }
}

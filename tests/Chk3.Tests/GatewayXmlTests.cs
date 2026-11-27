using System.Xml.Linq;
using Chk3.Autorizacao;

namespace Chk3.Tests;

/// <summary>O &lt;transaction-request&gt; gerado tem exatamente a estrutura e a ordem do modelo do Gateway (26/11).</summary>
public class GatewayXmlTests
{
    // Modelo enviado pelo Daniel, sem comentários e com valores fictícios (a merchantKey real nunca vai para o repo).
    const string Modelo = """
        <transaction-request><version>1.0.0</version>
        <verification><merchantId>00000000</merchantId><merchantKey>CHAVE-DE-TESTE</merchantKey><environment>1</environment></verification>
        <authorize><order><reference>PEDIDO-123456</reference><totalAmount>50000</totalAmount></order>
        <payment><acquirer>1</acquirer><method>1</method><amount>50000</amount><currency>986</currency><country>BRA</country>
        <numberOfPayments>1</numberOfPayments><groupNumber>0</groupNumber><flag>mastercard</flag><cardHolder>JOSE DA SILVA</cardHolder>
        <cardNumber>5555555555554444</cardNumber><cardSecurityCode>123</cardSecurityCode><cardExpirationDate>202805</cardExpirationDate>
        <saveCreditCard>false</saveCreditCard><generateToken>false</generateToken><departureTax>0</departureTax><softDescriptor>SUALOJA</softDescriptor></payment>
        <billing><customerIdentity>1</customerIdentity><name>Jose da Silva</name><address>Av. Paulista, 1000</address><address2>10 Andar</address2>
        <city>São Paulo</city><state>SP</state><postalCode>01310100</postalCode><country>BR</country><phone>11999990000</phone><email>josesilva@email.com</email></billing>
        <urlReturn>https://loja.exemplo/retorno</urlReturn><fraud>false</fraud><customField/></authorize></transaction-request>
        """;

    static readonly GatewayMerchant Merchant = new("00000000", "CHAVE-DE-TESTE", "1", "PADRAO", "2", false, "1");
    static readonly GatewayCard CardData = new("5555555555554444", "JOSE DA SILVA", "123", 5, 2028, "mastercard");

    [Fact]
    public void Igual_ao_modelo_com_todos_os_campos()
    {
        var billing = new Dictionary<string, string>
        {
            ["email"] = "josesilva@email.com", ["name"] = "Jose da Silva", ["customerIdentity"] = "1", ["address"] = "Av. Paulista, 1000",
            ["address2"] = "10 Andar", ["city"] = "São Paulo", ["state"] = "SP", ["postalCode"] = "01310100", ["country"] = "BR", ["phone"] = "11999990000",
        };
        var xml = GatewayXml.Build(Merchant, new GatewayOrder("PEDIDO-123456", 50000, "BRL", 1, billing, "https://loja.exemplo/retorno", "SUALOJA"), CardData);
        Assert.True(XNode.DeepEquals(XElement.Parse(Modelo), XElement.Parse(xml)), xml.Replace("CHAVE-DE-TESTE", "***"));
    }

    [Fact]
    public void Parcelado_usa_metodo_do_env_e_billing_vazio_sem_dados()
    {
        var x = XElement.Parse(GatewayXml.Build(Merchant, new GatewayOrder("P1", 1990, "BRL", 3, null, null, null), CardData));
        var p = x.Element("authorize")!.Element("payment")!;
        Assert.Equal("2", p.Element("method")!.Value);              // PENDENTE: código do parcelado não confirmado
        Assert.Equal("3", p.Element("numberOfPayments")!.Value);
        Assert.Equal("PADRAO", p.Element("softDescriptor")!.Value);  // padrão do .env
        Assert.False(x.Element("authorize")!.Element("billing")!.HasElements);
        Assert.Equal("", x.Element("authorize")!.Element("urlReturn")!.Value);
    }

    [Fact]
    public void Billing_parcial_omite_o_que_nao_foi_informado_e_usa_pais_BR()
    {
        var x = XElement.Parse(GatewayXml.Build(Merchant, new GatewayOrder("P1", 1000, "BRL", 1, new() { ["email"] = "a@b.com" }, null, null), CardData));
        Assert.Equal(["country", "email"], x.Element("authorize")!.Element("billing")!.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Environment_vazio_omite_o_elemento()
    {
        var x = XElement.Parse(GatewayXml.Build(Merchant with { Environment = null }, new GatewayOrder("P1", 1000, "BRL", 1, null, null, null), CardData));
        Assert.Equal(["merchantId", "merchantKey"], x.Element("verification")!.Elements().Select(e => e.Name.LocalName));
    }

    // Formato observado no Gateway real (27/11): XML de negócio serializado dentro de <string> (ASP.NET Web API).
    static XDocument Real(string code, string gwTx) => XDocument.Parse(
        "<string xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/\">" + System.Security.SecurityElement.Escape(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><transaction-response id=\"1\" versao=\"1.0\"><transactionId>{gwTx}</transactionId>" +
            $"<status><code>{code}</code><message>MSG - {code}</message></status></transaction-response>") + "</string>");

    [Fact]
    public void Resposta_real_embrulhada_le_codigo_e_referencia_e_mapeia_pelo_env()
    {
        Assert.Equal((null, "409", null), GatewayXml.ParseResponse(Real("409", "")));                 // sem mapeamento: indeterminado
        Assert.Equal(("approved", "0", "ABC123"), GatewayXml.ParseResponse(Real("0", "ABC123"), ["0"], ["409"]));
        Assert.Equal(("declined", "409", null), GatewayXml.ParseResponse(Real("409", ""), ["0"], ["409"]));
        Assert.Equal(("approved", "00", null), GatewayXml.ParseResponse(XDocument.Parse("<transaction-response><status>approved</status><code>00</code></transaction-response>")));
        Assert.Equal((null, null, null), GatewayXml.ParseResponse(XDocument.Parse("<Error><Message>x</Message></Error>")));
    }

    [Fact]
    public void Erro_do_gateway_real_le_codigo_e_details()
    {
        var inner = "<transaction-response id=\"1\"><transactionId /><status><code>409</code><message>ERROR - 409</message></status>" +
            "<result><error><code>1036</code><action>409</action><details>Autenticação no gateway inválido 5453010000066167</details></error></result></transaction-response>";
        var doc = XDocument.Parse("<string xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/\">" + System.Security.SecurityElement.Escape(inner) + "</string>");
        Assert.Equal(("1036", "Autenticação no gateway inválido ***"), GatewayXml.ParseError(doc));
        Assert.Equal((null, null), GatewayXml.ParseError(XDocument.Parse("<transaction-response><status>approved</status></transaction-response>")));
    }

    // Resposta processada observada (27/11, adquirente 27 E-REDE), com os dados já mascarados como no log.
    const string Processada = """
        <transaction-response id="1" versao="1.0"><transactionId>100-007-2026331-00630-200633676</transactionId>
        <operationId>1</operationId><status>4</status><message>UNAUTHORIZED</message><log>  </log><errorCode>  </errorCode>
        <order><reference>HML-1</reference><currency></currency><totalAmount>100</totalAmount><dateTime>2026-11-27T20:06:33+00:00</dateTime></order>
        <processors><processor><flag>mastercard</flag><amount>100</amount><numberOfPayments>1</numberOfPayments><acquirer>E-REDE</acquirer>
        <acquirerId>27</acquirerId><auth></auth><ref>711414668</ref><responseCode>NA</responseCode><descriptionResponse>NOT APPROVED</descriptionResponse>
        <transactionID>100126*********57455</transactionID><tokenCard>***</tokenCard></processor></processors></transaction-response>
        """;

    [Fact]
    public void Resposta_processada_le_status_numerico_ref_e_retorno_do_adquirente()
    {
        var doc = XDocument.Parse("<string xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/\">" + System.Security.SecurityElement.Escape(Processada) + "</string>");
        Assert.Equal((null, "4", "100-007-2026331-00630-200633676"), GatewayXml.ParseResponse(doc));                 // sem mapeamento
        Assert.Equal(("declined", "4", "100-007-2026331-00630-200633676"), GatewayXml.ParseResponse(doc, [], ["4"]));  // GATEWAY_DECLINED_CODES=4
        Assert.Equal(("NA", "27"), GatewayXml.ParseProcessor(doc));
        Assert.DoesNotContain("36146167", GatewayLogMask.Mask("<r><tokenCard>T702711000000036146167NHGNFKDL</tokenCard></r>"));
    }
}

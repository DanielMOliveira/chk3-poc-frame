using System.Xml.Linq;
using Chk3.Autorizacao;

namespace Chk3.Tests;

/// <summary>O &lt;transaction-request&gt; gerado tem exatamente a estrutura e a ordem do modelo do Gateway (26/11).</summary>
public class GatewayXmlTests
{
    // Modelo enviado pelo Daniel, sem comentários e com valores fictícios (a merchantKey real nunca vai para o repo).
    const string Modelo = """
        <transaction-request><version>1.0.0</version>
        <verification><merchantId>00000000</merchantId><merchantKey>CHAVE-DE-TESTE</merchantKey></verification>
        <authorize><order><reference>PEDIDO-123456</reference><totalAmount>50000</totalAmount></order>
        <payment><acquirer>1</acquirer><method>1</method><amount>50000</amount><currency>986</currency><country>BRA</country>
        <numberOfPayments>1</numberOfPayments><groupNumber>0</groupNumber><flag>mastercard</flag><cardHolder>JOSE DA SILVA</cardHolder>
        <cardNumber>5555555555554444</cardNumber><cardSecurityCode>123</cardSecurityCode><cardExpirationDate>202805</cardExpirationDate>
        <saveCreditCard>false</saveCreditCard><generateToken>false</generateToken><departureTax>0</departureTax><softDescriptor>SUALOJA</softDescriptor></payment>
        <billing><customerIdentity>1</customerIdentity><name>Jose da Silva</name><address>Av. Paulista, 1000</address><address2>10 Andar</address2>
        <city>São Paulo</city><state>SP</state><postalCode>01310100</postalCode><country>BR</country><phone>11999990000</phone><email>josesilva@email.com</email></billing>
        <urlReturn>https://loja.exemplo/retorno</urlReturn><fraud>false</fraud><customField/></authorize></transaction-request>
        """;

    static readonly GatewayMerchant Merchant = new("00000000", "CHAVE-DE-TESTE", "1", "PADRAO", "2", false);
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
}

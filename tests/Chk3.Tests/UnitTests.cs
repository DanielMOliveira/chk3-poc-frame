using Chk3.Shared;

namespace Chk3.Tests;

public class UnitTests
{
    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("4000000000000119", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("41111", false)]
    public void Luhn(string pan, bool ok) => Assert.Equal(ok, Card.Luhn(pan));

    [Theory]
    [InlineData("4111111111111111", "visa")]
    [InlineData("5555555555554444", "mastercard")]
    [InlineData("2221000000000009", "mastercard")]
    [InlineData("378282246310005", null)]
    public void Brand(string pan, string? brand) => Assert.Equal(brand, Card.Brand(pan));

    [Theory]
    [InlineData("cartao 4111 1111 1111 1111 aqui", true)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("pedido 123456789012", false)]
    [InlineData("texto livre", false)]
    public void PanPattern(string text, bool found) => Assert.Equal(found, Card.ContainsPanPattern(text));

    [Theory]
    [InlineData(1990, 3, 664, 663)]
    [InlineData(1000, 4, 250, 250)]
    [InlineData(50000, 12, 4174, 4166)]
    [InlineData(1, 1, 1, 1)]
    public void Parcelas_floor_e_resto_na_primeira(long total, int n, long first, long others)
    {
        Assert.Equal((first, others), Installments.Split(total, n));
        Assert.Equal(total, first + others * (n - 1));
    }

    [Fact]
    public void Parcelas_rotulo_e_maximo()
    {
        Assert.Equal("3x de R$ 6,63 sem juros (1ª de R$ 6,64)", Installments.Label(1990, 3));
        Assert.Equal("1x de R$ 1.234,56", Installments.Label(123456, 1));
        Assert.Equal("4x de R$ 2,50 sem juros", Installments.Label(1000, 4));
        Assert.Equal(2, Installments.MaxFor(2, 12));   // parcela mínima de 1 centavo
        Assert.False(Installments.IsValid(1990, 0, 6));
        Assert.False(Installments.IsValid(1990, 7, 6));
    }

    [Fact]
    public void Snapshot_assinado_rejeita_adulteracao_e_expiracao()
    {
        var signer = new SnapshotSigner(new byte[32]);
        var now = DateTimeOffset.UtcNow;
        var token = signer.Sign(new Snapshot(Guid.NewGuid(), ["visa"], 3, now.AddMinutes(5).ToUnixTimeSeconds()));
        Assert.NotNull(signer.Verify(token, now));

        var forged = signer.Sign(new Snapshot(Guid.NewGuid(), ["visa", "mastercard"], 12, now.AddMinutes(5).ToUnixTimeSeconds()));
        Assert.Null(signer.Verify(forged.Split('.')[0] + "." + token.Split('.')[1], now)); // corpo trocado
        Assert.Null(new SnapshotSigner(Enumerable.Repeat((byte)1, 32).ToArray()).Verify(token, now)); // outra chave
        Assert.Null(signer.Verify(token, now.AddMinutes(6))); // expirado
        Assert.Null(signer.Verify("lixo", now));
    }
}

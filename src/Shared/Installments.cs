namespace Chk3.Shared;

/// <summary>
/// Parcelamento no cartão de crédito (regra padrão BR, sem juros na V0). Valores em centavos:
/// parcela = floor(total / n), e o resto da divisão vai na primeira parcela.
/// Ex.: 1990 em 3x = 664 + 663 + 663.
/// </summary>
public static class Installments
{
    public const int ChannelDefaultMax = 12;

    /// <summary>Maior n permitido para o total: até o máximo da sessão e parcela de pelo menos 1 centavo.</summary>
    public static int MaxFor(long totalCents, int max) => (int)Math.Max(1, Math.Min(max, totalCents));

    public static bool IsValid(long totalCents, int n, int max) => n >= 1 && n <= MaxFor(totalCents, max);

    public static (long First, long Others) Split(long totalCents, int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
        long each = totalCents / n;
        return (each + totalCents % n, each);
    }

    /// <summary>"3x de R$ 6,63 sem juros (1ª de R$ 6,64)". À vista: "1x de R$ 19,90".</summary>
    public static string Label(long totalCents, int n)
    {
        if (n == 1) return $"1x de {Brl(totalCents)}";
        var (first, others) = Split(totalCents, n);
        return first == others ? $"{n}x de {Brl(others)} sem juros" : $"{n}x de {Brl(others)} sem juros (1ª de {Brl(first)})";
    }

    /// <summary>R$ 1.234,56 sem depender da cultura instalada no container.</summary>
    public static string Brl(long cents)
    {
        var neg = cents < 0; cents = Math.Abs(cents);
        var inteiro = (cents / 100).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '.');
        return $"{(neg ? "-" : "")}R$ {inteiro},{cents % 100:00}";
    }
}

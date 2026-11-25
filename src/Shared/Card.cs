using System.Text.RegularExpressions;

namespace Chk3.Shared;

public static partial class Card
{
    public static bool Luhn(string digits)
    {
        if (digits.Length < 12 || digits.Length > 19) return false;
        int sum = 0; bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (d < 0 || d > 9) return false;
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>Bandeira por BIN, só o necessário para o POC (visa e mastercard).</summary>
    public static string? Brand(string pan)
    {
        if (pan.Length is 13 or 16 or 19 && pan[0] == '4') return "visa";
        if (pan.Length == 16)
        {
            int p2 = int.Parse(pan[..2]); int p4 = int.Parse(pan[..4]);
            if (p2 is >= 51 and <= 55 || p4 is >= 2221 and <= 2720) return "mastercard";
        }
        return null;
    }

    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex CandidatePan();

    /// <summary>Passo 2a: detecta sequência que parece PAN (12 a 19 dígitos, com ou sem separador, Luhn válido).</summary>
    public static bool ContainsPanPattern(string text)
    {
        foreach (Match m in CandidatePan().Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (Luhn(digits)) return true;
        }
        return false;
    }
}

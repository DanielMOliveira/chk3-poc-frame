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

    // Faixas de BIN Elo conhecidas publicamente (6 dígitos). PENDÊNCIA: trocar pela tabela de BINs real do adquirente/Hub.
    static readonly (int From, int To)[] EloBins =
    [
        (401178, 401179), (431274, 431274), (438935, 438935), (451416, 451416), (457393, 457393), (457631, 457632),
        (504175, 504175), (506699, 506778), (509000, 509999), (627780, 627780), (636297, 636297), (636368, 636368),
        (650031, 650033), (650035, 650051), (650405, 650439), (650485, 650538), (650541, 650598), (650700, 650718),
        (650720, 650727), (650901, 650978), (651652, 651679), (655000, 655019), (655021, 655058),
    ];

    /// <summary>
    /// Bandeira por prefixo (POC): visa, mastercard (51-55 e BIN 2 2221-2720), amex, elo, diners, discover, jcb, aura,
    /// hipercard e hiper. Elo antes de visa/mastercard/discover (há BINs Elo em 4xxxxx, 50xxxx e 65xxxx).
    /// PENDÊNCIA: tabela de BINs real; hoje é só prefixo + tamanho.
    /// </summary>
    public static string? Brand(string pan)
    {
        if (pan.Length < 13 || !pan.All(char.IsAsciiDigit)) return null;
        int p1 = pan[0] - '0', p2 = int.Parse(pan[..2]), p3 = int.Parse(pan[..3]), p4 = int.Parse(pan[..4]), p6 = int.Parse(pan[..6]);
        int len = pan.Length;
        if (len is 16 && EloBins.Any(b => p6 >= b.From && p6 <= b.To)) return "elo";
        if (len is 16 or 19 && (p6 == 606282 || p4 == 3841)) return "hipercard";
        if (len is 16 && p6 is 637095 or 637568 or 637599 or 637609 or 637612) return "hiper";
        if (len is 19 && p2 == 50) return "aura";
        if (len is 15 && p2 is 34 or 37) return "amex";
        if (len is 14 && (p2 is 36 or 38 or 39 || p3 is >= 300 and <= 305)) return "diners";
        if (len is >= 16 and <= 19 && p4 is >= 3528 and <= 3589) return "jcb";
        if (len is >= 16 and <= 19 && (p4 == 6011 || p3 is >= 644 and <= 649 || p2 == 65)) return "discover";
        if (len is 16 && (p2 is >= 51 and <= 55 || p4 is >= 2221 and <= 2720)) return "mastercard";
        if (len is 13 or 16 or 19 && p1 == 4) return "visa";
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

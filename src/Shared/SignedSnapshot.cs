using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chk3.Shared;

/// <summary>Passo 7: snapshot de bandeiras da sessão. Formato POC: base64url(json).base64url(HMAC-SHA256).</summary>
public sealed record Snapshot(Guid Sid, string[] Brands, int MaxInstallments, long Exp);

public sealed class SnapshotSigner(byte[] key)
{
    public string Sign(Snapshot s)
    {
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(s));
        return body + "." + Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)));
    }

    /// <summary>Verifica assinatura e expiração. Retorna null se inválido ou expirado.</summary>
    public Snapshot? Verify(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        var parts = token.Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, FromBase64Url(parts[1]))) return null;
            var s = JsonSerializer.Deserialize<Snapshot>(FromBase64Url(parts[0]));
            if (s is null || s.Exp <= now.ToUnixTimeSeconds()) return null;
            return s;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

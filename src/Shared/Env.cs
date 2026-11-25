namespace Chk3.Shared;

public static class Env
{
    /// <summary>Lê variável obrigatória. Falha no arranque se faltar (fail fast, sem default inseguro).</summary>
    public static string Required(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Variável de ambiente obrigatória ausente: {name}");
        return v;
    }

    public static string Optional(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    /// <summary>Segredo com tamanho mínimo, para não subir com chave fraca.</summary>
    public static byte[] Secret(string name, int minBytes = 32)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(Required(name));
        if (b.Length < minBytes)
            throw new InvalidOperationException($"{name} precisa de pelo menos {minBytes} bytes");
        return b;
    }
}

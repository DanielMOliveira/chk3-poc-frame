using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chk3.Shared;

namespace Chk3.Tests;

/// <summary>Config dos testes de integração: variáveis de ambiente ou o .env da raiz do repo.</summary>
public static class TestEnv
{
    static readonly Dictionary<string, string> dotenv = LoadDotEnv();

    static Dictionary<string, string> LoadDotEnv()
    {
        var d = new Dictionary<string, string>();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(f)) continue;
            foreach (var line in File.ReadAllLines(f))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#') || !t.Contains('=')) continue;
                var i = t.IndexOf('=');
                d[t[..i]] = t[(i + 1)..];
            }
            break;
        }
        return d;
    }

    public static string Get(string name, string? fallback = null) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
        : dotenv.TryGetValue(name, out var dv) ? dv
        : fallback ?? throw new InvalidOperationException($"Defina {name}");

    // Mesma regra do docker-compose.yml: origem vazia deriva da porta do host.
    static string Origin(string name, string portVar, string port) =>
        Get(name, "") is { Length: > 0 } o ? o.TrimEnd('/') : $"http://localhost:{Get(portVar, port)}";

    /// <summary>Origens públicas (o que o navegador vê; usadas para conferir CSP).</summary>
    public static string CanalOrigin => Origin("CANAL_PUBLIC_ORIGIN", "CANAL_HOST_PORT", "8080");
    public static string OpcoesOrigin => Origin("OPCOES_PUBLIC_ORIGIN", "OPCOES_HOST_PORT", "8081");
    public static string CartaoOrigin => Origin("CARTAO_PUBLIC_ORIGIN", "CARTAO_HOST_PORT", "8082");

    /// <summary>URLs de acesso dos testes. No ./scripts/test.sh apontam para os serviços na rede docker.</summary>
    public static string Integration => Get("INTEGRATION_URL", "") is { Length: > 0 } u ? u : $"http://localhost:{Get("INTEGRATION_HOST_PORT", "8083")}";
    public static string Opcoes => Get("TEST_OPCOES_URL", "") is { Length: > 0 } u ? u : OpcoesOrigin;
    public static string Cartao => Get("TEST_CARTAO_URL", "") is { Length: > 0 } u ? u : CartaoOrigin;
}

public sealed record Session(int Status, Guid TransactionId, string? State, string? FrameUrl, string? PaymentCredential);

/// <summary>Cliente que faz o papel do canal e do navegador nos passos do frame.</summary>
public sealed class Flow : IDisposable
{
    public readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string Jwt(string channel = "canal-fake", string? secret = null, string scope = "sessions:create", long? exp = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string B(byte[] b) => SnapshotSigner.Base64Url(b);
        var h = B(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var p = B(JsonSerializer.SerializeToUtf8Bytes(new { iss = TestEnv.Get("CHANNEL_JWT_ISSUER"), aud = "chk3-integration", sub = channel, scope, iat = now, exp = exp ?? now + 300 }));
        var key = Encoding.UTF8.GetBytes(secret ?? TestEnv.Get("CHANNEL_JWT_SECRET"));
        return $"{h}.{p}.{B(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(h + "." + p)))}";
    }

    public static object Body(string order = "PED-T", long amount = 1000, int? installments = null, Dictionary<string, string>? campos = null) =>
        new { orderId = order, amountCents = amount, currency = "BRL", installments, camposAdicionais = campos ?? new() { ["origem"] = "teste" } };

    public async Task<HttpResponseMessage> PostSessionRaw(object body, string? key = null, string? jwt = null, string path = "/sessions")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, TestEnv.Integration + path) { Content = JsonContent.Create(body) };
        if (jwt != "") req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt ?? Jwt());
        req.Headers.Add("Idempotency-Key", key ?? NewKey());
        return await Http.SendAsync(req);
    }

    public async Task<Session> CreateSession(object? body = null, string? key = null, string channel = "canal-fake")
    {
        using var r = await PostSessionRaw(body ?? Body(), key, Jwt(channel));
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        string? S(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new((int)r.StatusCode, j.TryGetProperty("transactionId", out var t) ? t.GetGuid() : Guid.Empty, S("state"), S("frameUrl"), S("paymentCredential"));
    }

    public static string NewKey() => "teste-" + Guid.NewGuid().ToString("N");

    public async Task<HttpResponseMessage> Step6(string pc) => await Post(TestEnv.Opcoes + "/api/session", "X-Payment-Credential", pc);

    public async Task<(int Status, string? Tc)> Step7(string pc)
    {
        using var r = await Post(TestEnv.Opcoes + "/api/typing-credential", "X-Payment-Credential", pc);
        if (!r.IsSuccessStatusCode) return ((int)r.StatusCode, null);
        return ((int)r.StatusCode, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("typingCredential").GetString());
    }

    public async Task<(int Status, string? Snapshot)> Step8(string tc, int installments = 1)
    {
        using var r = await Http.PostAsync(TestEnv.Cartao + "/digitacao", new FormUrlEncodedContent(new Dictionary<string, string> { ["tc"] = tc, ["installments"] = installments.ToString() }));
        var html = await r.Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"chk3-snapshot\" content=\"([^\"]+)\"");
        return ((int)r.StatusCode, m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null);
    }

    public async Task<(int Status, string? Result)> Step9(string tc, string snapshot, string pan, int installments = 1, string cvv = "123")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, TestEnv.Cartao + "/api/authorize")
        {
            Content = JsonContent.Create(new { pan, expMonth = 12, expYear = DateTime.UtcNow.Year + 2, cvv, installments, snapshot, deviceSessionId = "teste-device" }),
        };
        req.Headers.Add("X-Typing-Credential", tc);
        using var r = await Http.SendAsync(req);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        return ((int)r.StatusCode, j.GetProperty("status").GetString());
    }

    /// <summary>Do POST /sessions até a página de digitação (passos 1-8).</summary>
    public async Task<(Session S, string Tc, string Snapshot)> UpToStep8(string channel = "canal-fake", object? body = null)
    {
        var s = await CreateSession(body, channel: channel);
        Assert.Equal(201, s.Status);
        var (_, tc) = await Step7(s.PaymentCredential!);
        var (_, snap) = await Step8(tc!);
        return (s, tc!, snap!);
    }

    async Task<HttpResponseMessage> Post(string url, string header, string value)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add(header, value);
        return await Http.SendAsync(req);
    }

    public void Dispose() => Http.Dispose();
}

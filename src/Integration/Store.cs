using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace Chk3.Integration;

public sealed class SessionRow
{
    public Guid TransactionId { get; init; }
    public string ChannelId { get; init; } = "";
    public byte[] Fingerprint { get; init; } = [];
    public string OrderId { get; init; } = "";
    public long AmountCents { get; init; }
    public string Currency { get; init; } = "";
    public int? Installments { get; init; }
    public string[] Brands { get; init; } = [];
    public int MaxInstallments { get; init; }
    public string State { get; init; } = "";
    public DateTime ExpiresAt { get; init; }
    public string DisplayName { get; init; } = "";
}

public sealed class ChannelRow
{
    public string Id { get; init; } = "";
    public string MerchantId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int SessionTtlSeconds { get; init; }
    public int MaxInstallments { get; init; }
    public string[] Brands { get; init; } = [];
}

public sealed class AttemptRow
{
    public Guid LocalRef { get; init; }
    public string Stage { get; init; } = "";
    public string? Result { get; init; }
}

/// <summary>Camada fina de dados (Npgsql + Dapper). Só hash de credencial vai para a base.</summary>
public sealed class Store(NpgsqlDataSource db)
{
    public const string SessionCols = """
        p.transaction_id AS TransactionId, p.channel_id AS ChannelId, p.fingerprint AS Fingerprint,
        p.order_id AS OrderId, p.amount_cents AS AmountCents, p.currency AS Currency,
        p.installments AS Installments, p.brands AS Brands, p.max_installments AS MaxInstallments,
        p.state AS State, p.expires_at AS ExpiresAt, ch.display_name AS DisplayName
        """;

    public NpgsqlDataSource Db => db;

    public static (string Plain, byte[] Hash) NewCredential(string prefix)
    {
        var plain = prefix + Chk3.Shared.SnapshotSigner.Base64Url(RandomNumberGenerator.GetBytes(32));
        return (plain, Hash(plain));
    }

    public static byte[] Hash(string plain) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plain));

    public async Task<ChannelRow?> GetChannelAsync(string id)
    {
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<ChannelRow>("""
            SELECT id AS Id, merchant_id AS MerchantId, display_name AS DisplayName,
                   session_ttl_seconds AS SessionTtlSeconds, max_installments AS MaxInstallments, brands AS Brands
            FROM channels WHERE id = @id AND active
            """, new { id });
    }

    /// <summary>Valida sem consumir (passos 6, 7, 8): existe, no TTL, canal ativo, sessão sem tentativa.</summary>
    public async Task<SessionRow?> FindOpenSessionByCredentialAsync(string? plain, string kind)
    {
        if (string.IsNullOrEmpty(plain) || plain.Length > 128) return null;
        await using var c = await db.OpenConnectionAsync();
        return await c.QuerySingleOrDefaultAsync<SessionRow>($"""
            SELECT {SessionCols}
            FROM credentials cr
            JOIN payment_sessions p ON p.transaction_id = cr.transaction_id
            JOIN channels ch ON ch.id = p.channel_id AND ch.active
            WHERE cr.hash = @hash AND cr.kind = @kind
              AND cr.expires_at > now() AND p.expires_at > now()
              AND p.state = 'Criada'
              AND NOT EXISTS (SELECT 1 FROM attempts a WHERE a.transaction_id = p.transaction_id)
            """, new { hash = Hash(plain), kind });
    }

    public async Task InsertCredentialAsync(Guid tx, byte[] hash, string kind, DateTime expiresAt, NpgsqlConnection? conn = null, NpgsqlTransaction? t = null)
    {
        const string sql = "INSERT INTO credentials(hash, transaction_id, kind, expires_at) VALUES (@hash, @tx, @kind, @expiresAt)";
        if (conn is not null) { await conn.ExecuteAsync(sql, new { hash, tx, kind, expiresAt }, t); return; }
        await using var c = await db.OpenConnectionAsync();
        await c.ExecuteAsync(sql, new { hash, tx, kind, expiresAt });
    }
}

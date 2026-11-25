using System.Reflection;
using Dapper;
using Npgsql;

namespace Chk3.Integration;

/// <summary>Runner mínimo: aplica Migrations/*.sql (embutidos) em ordem, uma vez, com advisory lock.</summary>
public static class Migrator
{
    public static async Task RunAsync(NpgsqlDataSource db, ILogger log)
    {
        await using var conn = await OpenWithRetryAsync(db, log);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(727001)");
        try
        {
            await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
            var applied = (await conn.QueryAsync<string>("SELECT version FROM schema_migrations")).ToHashSet();
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order())
            {
                var version = res.Split('.')[^2];
                if (applied.Contains(version)) continue;
                using var reader = new StreamReader(asm.GetManifestResourceStream(res)!);
                var sql = await reader.ReadToEndAsync();
                await using var tx = await conn.BeginTransactionAsync();
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations(version) VALUES (@version)", new { version }, tx);
                await tx.CommitAsync();
                log.LogInformation("Migração aplicada: {Version}", version);
            }
        }
        finally { await conn.ExecuteAsync("SELECT pg_advisory_unlock(727001)"); }
    }

    private static async Task<NpgsqlConnection> OpenWithRetryAsync(NpgsqlDataSource db, ILogger log)
    {
        for (var i = 1; ; i++)
        {
            try { return await db.OpenConnectionAsync(); }
            catch (Exception ex) when (i < 30 && ex is NpgsqlException or System.Net.Sockets.SocketException)
            {
                log.LogWarning("Postgres indisponível (tentativa {I}), aguardando", i);
                await Task.Delay(1000);
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chk3.Shared;
using Dapper;
using Npgsql;

// Worker (passos 15 e 16 do v7, versão mínima do POC). Uma réplica. Dois laços:
//  - passo 16: entrega o outbox como webhook ao canal (URL do cadastro do canal ou WEBHOOK_DEFAULT_URL), assinado com HMAC;
//  - expurgo do token do frame (avaliação CHK-5): apaga em lotes pequenos os tokens vencidos ou já usados e guarda
//    só metadados sanitizados em frame_token_history.
// Payload do webhook: sem PAN, CVV, validade, nome, billing, credenciais ou hash.
var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("worker");
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var cfg = new WorkerConfig(
    DefaultUrl: Env.Optional("WEBHOOK_DEFAULT_URL", ""),
    SigningKey: Env.Secret("WEBHOOK_SIGNING_SECRET"),
    PollMs: int.Parse(Env.Optional("WORKER_POLL_MS", "2000")),
    PurgeEverySeconds: int.Parse(Env.Optional("PURGE_INTERVAL_SECONDS", "60")),
    PurgeBatch: Math.Clamp(int.Parse(Env.Optional("PURGE_BATCH_SIZE", "500")), 1, 4000),
    PurgeGraceSeconds: int.Parse(Env.Optional("PURGE_USED_GRACE_SECONDS", "600")),
    MaxAttempts: int.Parse(Env.Optional("WEBHOOK_MAX_ATTEMPTS", "8")));
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(NpgsqlDataSource.Create(Env.Required("POSTGRES_CONNECTION")));
builder.Services.AddHttpClient("webhook", c => { c.Timeout = TimeSpan.FromSeconds(10); c.DefaultRequestHeaders.UserAgent.ParseAdd("chk3-poc-worker/1.0"); })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<WebhookLoop>();
builder.Services.AddHostedService<PurgeLoop>();

var app = builder.Build();
app.UseChk3Trace();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public sealed record WorkerConfig(string DefaultUrl, byte[] SigningKey, int PollMs, int PurgeEverySeconds, int PurgeBatch, int PurgeGraceSeconds, int MaxAttempts);

public sealed class OutboxRow
{
    public long Id { get; init; }
    public Guid EventId { get; init; }
    public Guid TransactionId { get; init; }
    public string EventType { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public int Attempts { get; init; }
    public string OrderId { get; init; } = "";
    public string State { get; init; } = "";
    public long AmountCents { get; init; }
    public string Currency { get; init; } = "";
    public int? Installments { get; init; }
    public string CamposAdicionais { get; init; } = "{}";
    public string? WebhookUrl { get; init; }
}

/// <summary>Passo 16: webhook com retentativa e backoff. Claim com FOR UPDATE SKIP LOCKED (seguro com N réplicas).</summary>
sealed class WebhookLoop(NpgsqlDataSource db, IHttpClientFactory http, WorkerConfig cfg, ILogger<WebhookLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { while (await DeliverOneAsync(ct)) { } }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException) { log.LogWarning("Outbox indisponível: {Tipo}", ex.GetType().Name); }
            await Task.Delay(cfg.PollMs, ct);
        }
    }

    async Task<bool> DeliverOneAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var e = await conn.QuerySingleOrDefaultAsync<OutboxRow>("""
            SELECT o.id AS Id, o.event_id AS EventId, o.transaction_id AS TransactionId, o.event_type AS EventType,
                   o.created_at AS CreatedAt, o.attempts AS Attempts, p.order_id AS OrderId, p.state AS State,
                   p.amount_cents AS AmountCents, p.currency AS Currency, a.installments AS Installments,
                   p.campos_adicionais::text AS CamposAdicionais, ch.webhook_url AS WebhookUrl
            FROM outbox o
            JOIN payment_sessions p ON p.transaction_id = o.transaction_id
            JOIN channels ch ON ch.id = p.channel_id
            LEFT JOIN attempts a ON a.transaction_id = o.transaction_id
            WHERE o.delivered_at IS NULL AND o.next_attempt_at <= now()
            ORDER BY o.id
            LIMIT 1
            FOR UPDATE OF o SKIP LOCKED
            """, transaction: tx);
        if (e is null) return false;

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var url = e.WebhookUrl ?? cfg.DefaultUrl;
        int? status = null; string erro;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            erro = "sem_url_https"; // nada configurado: não tenta de novo até alguém configurar e reprocessar
            await conn.ExecuteAsync("UPDATE outbox SET next_attempt_at = 'infinity', last_error = @erro WHERE id = @id", new { erro, id = e.Id }, tx);
            await tx.CommitAsync(ct);
            Chk3Trace.Event("16", e.TransactionId, "webhook_" + erro, null, Chk3Trace.Since(t0), e.OrderId);
            return true;
        }

        // Payload público (webhook.site): só dados do pagamento, sem dado de cartão, comprador ou credencial.
        var body = JsonSerializer.Serialize(new
        {
            eventId = e.EventId, eventType = e.EventType, occurredAt = DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc),
            transactionId = e.TransactionId, orderId = e.OrderId, state = e.State, amount = e.AmountCents,
            currency = e.Currency, installments = e.Installments,
            camposAdicionais = JsonSerializer.Deserialize<Dictionary<string, string>>(e.CamposAdicionais),
        });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(cfg.SigningKey, Encoding.UTF8.GetBytes(ts + "." + body)));
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Chk3-Event-Id", e.EventId.ToString());
        req.Headers.Add("X-Chk3-Event-Type", e.EventType);
        req.Headers.Add("X-Chk3-Signature", $"t={ts},v1={sig}"); // HMAC-SHA256(segredo, "t.corpo"); nunca logado
        try
        {
            using var resp = await http.CreateClient("webhook").SendAsync(req, ct);
            status = (int)resp.StatusCode;
            erro = resp.IsSuccessStatusCode ? "" : "http_" + status;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { erro = ex is TaskCanceledException ? "timeout" : "rede"; }

        var attempts = e.Attempts + 1;
        if (erro == "")
        {
            await conn.ExecuteAsync("UPDATE outbox SET delivered_at = now(), attempts = @attempts, last_status = @status, last_error = NULL WHERE id = @id",
                new { attempts, status, id = e.Id }, tx);
            await tx.CommitAsync(ct);
            Chk3Trace.Event("16", e.TransactionId, $"webhook_entregue_{uri.Host}_http{status}_tentativa{attempts}", status, Chk3Trace.Since(t0), e.OrderId);
        }
        else
        {
            var desiste = attempts >= cfg.MaxAttempts;
            await conn.ExecuteAsync("""
                UPDATE outbox SET attempts = @attempts, last_status = @status, last_error = @erro,
                    next_attempt_at = CASE WHEN @desiste THEN 'infinity'::timestamptz ELSE now() + make_interval(secs => @backoff) END
                WHERE id = @id
                """, new { attempts, status, erro, desiste, backoff = (double)Math.Min(3600, 5 * Math.Pow(2, attempts - 1)), id = e.Id }, tx);
            await tx.CommitAsync(ct);
            Chk3Trace.Event("16", e.TransactionId, $"webhook_falhou_{uri.Host}_{erro}_tentativa{attempts}{(desiste ? "_desistiu" : "")}", status, Chk3Trace.Since(t0), e.OrderId);
        }
        return true;
    }
}

/// <summary>Expurgo do token do frame em lotes pequenos (abaixo de 5.000 linhas por comando). A validação não depende dele.</summary>
sealed class PurgeLoop(NpgsqlDataSource db, WorkerConfig cfg, ILogger<PurgeLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int total = 0, n;
                do
                {
                    await using var conn = await db.OpenConnectionAsync(ct);
                    n = await conn.ExecuteAsync("""
                        WITH v AS (
                            SELECT hash FROM frame_tokens
                            WHERE expires_at < now() OR used_at < now() - make_interval(secs => @grace)
                            ORDER BY expires_at
                            LIMIT @batch
                            FOR UPDATE SKIP LOCKED),
                        d AS (
                            DELETE FROM frame_tokens t USING v WHERE t.hash = v.hash
                            RETURNING t.transaction_id, t.kind, t.created_at, t.expires_at, t.used_at)
                        INSERT INTO frame_token_history (transaction_id, kind, created_at, expires_at, used_at, motivo)
                        SELECT transaction_id, kind, created_at, expires_at, used_at,
                               CASE WHEN used_at IS NOT NULL THEN 'usado' ELSE 'expirado' END
                        FROM d
                        """, new { grace = (double)cfg.PurgeGraceSeconds, batch = cfg.PurgeBatch });
                    total += n;
                } while (n == cfg.PurgeBatch && !ct.IsCancellationRequested);
                if (total > 0) Chk3Trace.Event("15", null, $"expurgo_token_frame_{total}_para_historico", null, Chk3Trace.Since(t0));
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException) { log.LogWarning("Expurgo indisponível: {Tipo}", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(cfg.PurgeEverySeconds), ct);
        }
    }
}

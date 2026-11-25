using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chk3.Shared;
using Dapper;

namespace Chk3.Integration;

public sealed record CreateSessionRequest(
    string? OrderId, long AmountCents, string? Currency, int? Installments,
    Dictionary<string, string>? CamposAdicionais);

public sealed record TypingCredentialRequest(string? Method);
public sealed record AttemptRequest(Guid TransactionId, string? Brand, int Installments);
public sealed class CreatedRow { public Guid Id { get; init; } public DateTime ExpiresAt { get; init; } }
public sealed record ResultRequest(string? Result, string? GatewayCode);

public static partial class Endpoints
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")] private static partial Regex OrderIdRx();
    [GeneratedRegex("^[A-Za-z0-9_]{1,40}$")] private static partial Regex CampoKeyRx();
    [GeneratedRegex("^[\\x21-\\x7E]{8,128}$")] private static partial Regex IdemKeyRx();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")] private static partial Regex GatewayCodeRx();

    static IResult Err(int status, string code) => Results.Json(new { erro = code }, statusCode: status);

    // ---------- Passos 1 a 4: POST /sessions ----------
    public static async Task<IResult> CreateSession(HttpContext ctx, CreateSessionRequest? req, Store store, IntegrationConfig cfg)
    {
        string idemKey = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (!IdemKeyRx().IsMatch(idemKey)) return Err(400, "idempotency_key_invalida");
        if (req is null) return Err(400, "corpo_invalido");

        // Passo 2: autoriza cliente/merchant (canal = sub do JWT, precisa estar ativo no cadastro).
        var channelId = ctx.User.FindFirst("sub")!.Value;
        var channel = await store.GetChannelAsync(channelId);
        if (channel is null) return Err(403, "canal_nao_autorizado");

        if (req.OrderId is null || !OrderIdRx().IsMatch(req.OrderId) || Card.ContainsPanPattern(req.OrderId))
            return Err(400, "pedido_invalido");
        if (req.AmountCents is <= 0 or > 1_000_000_000) return Err(400, "valor_invalido");
        if (req.Currency != "BRL") return Err(400, "moeda_invalida");

        // 2a: camposAdicionais fora dos limites ou com padrão de PAN. Não registra e não loga o valor.
        var campos = req.CamposAdicionais ?? new();
        if (campos.Count > 10 || campos.Any(kv => !CampoKeyRx().IsMatch(kv.Key) || kv.Value is null
                || kv.Value.Length > 255 || Card.ContainsPanPattern(kv.Value)))
            return Err(400, "campos_adicionais_invalidos");

        // 2b: parcelas acima do máximo do cadastro do canal.
        if (req.Installments is { } n && (n < 1 || n > channel.MaxInstallments)) return Err(400, "parcelas_invalidas");

        // Passo 3: fingerprint do conteúdo para a admissão idempotente.
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            req.OrderId, req.AmountCents, req.Currency, req.Installments,
            Campos = new SortedDictionary<string, string>(campos, StringComparer.Ordinal),
        }));

        await using var conn = await store.Db.OpenConnectionAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            // 3c: admissão atômica por canal + chave. ON CONFLICT resolve a corrida entre réplicas.
            var created = await conn.QuerySingleOrDefaultAsync<CreatedRow>("""
                INSERT INTO payment_sessions (transaction_id, channel_id, idempotency_key, fingerprint, order_id,
                    amount_cents, currency, installments, campos_adicionais, brands, max_installments, expires_at)
                VALUES (@id, @channelId, @idemKey, @fingerprint, @orderId, @amount, @currency, @installments,
                    CAST(@campos AS jsonb), @brands, @maxInst, now() + make_interval(secs => @ttl))
                ON CONFLICT (channel_id, idempotency_key) DO NOTHING
                RETURNING transaction_id AS Id, expires_at AS ExpiresAt
                """, new
                {
                    id = Guid.CreateVersion7(), channelId, idemKey, fingerprint, orderId = req.OrderId,
                    amount = req.AmountCents, currency = req.Currency, installments = req.Installments,
                    campos = JsonSerializer.Serialize(campos), brands = channel.Brands,
                    maxInst = channel.MaxInstallments, ttl = (double)channel.SessionTtlSeconds,
                }, tx);

            if (created is { } c)
            {
                var (plain, hash) = Store.NewCredential("pc_");
                await store.InsertCredentialAsync(c.Id, hash, "pagamento", c.ExpiresAt, conn, tx);
                await tx.CommitAsync();
                return Results.Json(SessionResponse(cfg, c.Id, "Criada", c.ExpiresAt, plain), statusCode: 201);
            }
            await tx.RollbackAsync();
        }

        // Chave já usada: 3a ou 3b.
        var existing = await conn.QuerySingleAsync<SessionRow>($"""
            SELECT {Store.SessionCols} FROM payment_sessions p JOIN channels ch ON ch.id = p.channel_id
            WHERE p.channel_id = @channelId AND p.idempotency_key = @idemKey
            """, new { channelId, idemKey });

        if (!CryptographicOperations.FixedTimeEquals(existing.Fingerprint, fingerprint))
            return Err(409, "idempotency_key_com_conteudo_diferente"); // 3a

        // 3b: sessão aberta, no TTL e sem tentativa: nova credencial, mesmo TTL (não estende).
        var hasAttempt = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM attempts WHERE transaction_id = @id)", new { id = existing.TransactionId });
        if (existing.State == "Criada" && !hasAttempt && existing.ExpiresAt > DateTime.UtcNow)
        {
            var (plain, hash) = Store.NewCredential("pc_");
            await store.InsertCredentialAsync(existing.TransactionId, hash, "pagamento", existing.ExpiresAt, conn);
            return Results.Json(SessionResponse(cfg, existing.TransactionId, existing.State, existing.ExpiresAt, plain), statusCode: 200);
        }
        var state = existing.State == "Criada" && existing.ExpiresAt <= DateTime.UtcNow ? "Expirada" : existing.State;
        return Results.Json(SessionResponse(cfg, existing.TransactionId, state, existing.ExpiresAt, null), statusCode: 200);
    }

    static object SessionResponse(IntegrationConfig cfg, Guid id, string state, DateTime expiresAt, string? credential) => new
    {
        transactionId = id,
        state,
        expiresAt = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
        // Passo 4: credencial no fragment (nunca na query). O valor nunca vai na URL.
        frameUrl = credential is null ? null : $"{cfg.OpcoesOrigin}/#pc={credential}",
        paymentCredential = credential,
    };

    // ---------- Passo 6: valida credencial de pagamento sem consumir ----------
    public static async Task<IResult> ValidatePaymentCredential(HttpContext ctx, Store store)
    {
        var s = await store.FindOpenSessionByCredentialAsync(ctx.Request.Headers[Web.PaymentCredentialHeader], "pagamento");
        if (s is null) return Err(401, "recusada"); // 6a sem detalhes
        // 6b: dados só para exibição.
        return Results.Ok(new
        {
            transactionId = s.TransactionId,
            merchantName = s.DisplayName,
            methods = new[] { "cartao_credito" }, // Google Pay: desenho separado (fora do POC)
            brands = s.Brands,
            maxInstallments = s.MaxInstallments,
            fixedInstallments = s.Installments,
            amountCents = s.AmountCents,
            currency = s.Currency,
            orderId = s.OrderId,
        });
    }

    // ---------- Passo 7: revalida e emite credencial de digitação + snapshot assinado ----------
    public static async Task<IResult> IssueTypingCredential(HttpContext ctx, TypingCredentialRequest? req, Store store, SnapshotSigner signer)
    {
        if (req?.Method != "cartao_credito") return Err(400, "meio_invalido");
        var s = await store.FindOpenSessionByCredentialAsync(ctx.Request.Headers[Web.PaymentCredentialHeader], "pagamento");
        if (s is null) return Err(401, "recusada");

        await using var conn = await store.Db.OpenConnectionAsync();
        var issued = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM credentials WHERE transaction_id = @id AND kind = 'digitacao'", new { id = s.TransactionId });
        if (issued >= 20) return Err(429, "limite_credenciais"); // evita crescimento sem limite por sessão

        var (plain, hash) = Store.NewCredential("tc_");
        await store.InsertCredentialAsync(s.TransactionId, hash, "digitacao", s.ExpiresAt, conn); // expira junto com a sessão
        return Results.Ok(new
        {
            typingCredential = plain,
            snapshot = signer.Sign(ToSnapshot(s)),
            expiresAt = DateTime.SpecifyKind(s.ExpiresAt, DateTimeKind.Utc),
        });
    }

    static Snapshot ToSnapshot(SessionRow s) => new(s.TransactionId, s.Brands, s.MaxInstallments,
        new DateTimeOffset(DateTime.SpecifyKind(s.ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds());

    // ---------- Passo 8: valida credencial de digitação sem consumir e devolve snapshot ----------
    public static async Task<IResult> ValidateTypingCredential(HttpContext ctx, Store store, SnapshotSigner signer)
    {
        var s = await store.FindOpenSessionByCredentialAsync(ctx.Request.Headers[Web.TypingCredentialHeader], "digitacao");
        if (s is null) return Err(401, "recusada");
        // Valor e moeda só para exibição na página de digitação (o navegador nunca envia valor no passo 9).
        return Results.Ok(new
        {
            transactionId = s.TransactionId, snapshot = signer.Sign(ToSnapshot(s)), fixedInstallments = s.Installments,
            maxInstallments = s.MaxInstallments, brands = s.Brands, amountCents = s.AmountCents, currency = s.Currency,
        });
    }

    // ---------- Passo 10: tentativa única atômica, sem PAN ----------
    public static async Task<IResult> RegisterAttempt(HttpContext ctx, AttemptRequest? req, Store store)
    {
        string? plain = ctx.Request.Headers[Web.TypingCredentialHeader];
        if (req is null || string.IsNullOrEmpty(plain) || plain.Length > 128) return Err(422, "sessao_invalida");

        await using var conn = await store.Db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Lock na sessão: serializa tentativas concorrentes da mesma sessão.
        var s = await conn.QuerySingleOrDefaultAsync<SessionRow>($"""
            SELECT {Store.SessionCols}
            FROM credentials cr
            JOIN payment_sessions p ON p.transaction_id = cr.transaction_id
            JOIN channels ch ON ch.id = p.channel_id
            WHERE cr.hash = @hash AND cr.kind = 'digitacao'
            FOR UPDATE OF p
            """, new { hash = Store.Hash(plain) }, tx);
        if (s is null) return Err(422, "sessao_invalida"); // 10a

        var attempt = await conn.QuerySingleOrDefaultAsync<AttemptRow>(
            "SELECT local_ref AS LocalRef, stage AS Stage, result AS Result FROM attempts WHERE transaction_id = @id",
            new { id = s.TransactionId }, tx);
        if (attempt is not null) // 10b: tentativa repetida, estado conhecido, sem novo envio
            return Results.Json(new { erro = "tentativa_ja_registrada", state = s.State }, statusCode: 409);

        var credOk = await conn.ExecuteScalarAsync<bool>(
            "SELECT expires_at > now() FROM credentials WHERE hash = @hash", new { hash = Store.Hash(plain) }, tx);
        bool installmentsOk = req.Installments >= 1 && req.Installments <= s.MaxInstallments
                              && (s.Installments is null || s.Installments == req.Installments);
        if (!credOk || req.TransactionId != s.TransactionId || s.ExpiresAt <= DateTime.UtcNow || s.State != "Criada"
            || req.Brand is null || !s.Brands.Contains(req.Brand) || !installmentsOk)
            return Err(422, "sessao_invalida"); // 10a: sessão inválida/divergente do snapshot, bandeira fora do snapshot ou parcelas inválidas

        // 10c: registra a tentativa única e muda o estado.
        var localRef = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO attempts (transaction_id, local_ref, brand, installments, stage)
            VALUES (@id, @localRef, @brand, @inst, 'envio_iniciado');
            UPDATE payment_sessions SET state = 'AutorizacaoSolicitada', updated_at = now() WHERE transaction_id = @id;
            """, new { id = s.TransactionId, localRef, brand = req.Brand, inst = req.Installments }, tx);
        await tx.CommitAsync();

        // Valores do servidor, nunca do navegador.
        return Results.Json(new
        {
            transactionId = s.TransactionId, localRef, amountCents = s.AmountCents, currency = s.Currency,
            installments = req.Installments, orderId = s.OrderId,
        }, statusCode: 201);
    }

    // ---------- Passo 13: registra resultado, evidência sanitizada e outbox atômicos ----------
    public static async Task<IResult> RegisterResult(Guid localRef, ResultRequest? req, Store store)
    {
        if (req?.Result is not ("Autorizada" or "Recusada" or "Indeterminada")) return Err(400, "resultado_invalido");
        var code = req.GatewayCode is { } g && GatewayCodeRx().IsMatch(g) ? g : null;

        await using var conn = await store.Db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var txId = await conn.QuerySingleOrDefaultAsync<Guid?>("""
            UPDATE attempts SET stage = 'resultado_registrado', result = @result, gateway_code = @code, updated_at = now()
            WHERE local_ref = @localRef AND stage = 'envio_iniciado'
            RETURNING transaction_id
            """, new { localRef, result = req.Result, code }, tx);
        if (txId is null)
        {
            var prev = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT result FROM attempts WHERE local_ref = @localRef", new { localRef }, tx);
            return prev == req.Result ? Results.Ok(new { state = prev }) : Err(409, "resultado_ja_registrado");
        }
        await conn.ExecuteAsync("""
            UPDATE payment_sessions SET state = @result, updated_at = now() WHERE transaction_id = @txId;
            INSERT INTO outbox (transaction_id, event_type, payload)
            VALUES (@txId, 'pagamento.' || lower(@result), jsonb_build_object('transactionId', @txId, 'state', @result));
            """, new { txId, result = req.Result }, tx);
        await tx.CommitAsync();
        return Results.Ok(new { state = req.Result });
    }
}

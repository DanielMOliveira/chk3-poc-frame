using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chk3.Shared;
using Dapper;

namespace Chk3.Integration;

/// <summary>Dados do comprador (opcionais). Nomes iguais aos do &lt;billing&gt; do Gateway. Nunca vão para log.</summary>
public sealed record BillingInput(
    string? CustomerIdentity, string? Name, string? Address, string? Address2, string? City,
    string? State, string? PostalCode, string? Country, string? Phone, string? Email);

/// <summary>Corpo do POST /sessions (JSON). Ata 26/11: só o valor total (amount, em centavos) é obrigatório.</summary>
public sealed record CreateSessionRequest(
    long? Amount, string? OrderId, string? Currency, int? Installments, BillingInput? Billing,
    string? UrlReturn, string? SoftDescriptor, Dictionary<string, string>? CamposAdicionais);

public sealed record TypingCredentialRequest(string? Method);
public sealed record AttemptRequest(Guid TransactionId, string? Brand, int Installments);
public sealed class CreatedRow { public Guid Id { get; init; } public DateTime ExpiresAt { get; init; } }
public sealed record ResultRequest(string? Result, string? GatewayCode);
public sealed class PaymentExtras { public string? Billing { get; init; } public string? UrlReturn { get; init; } public string? SoftDescriptor { get; init; } }

public static partial class Endpoints
{
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")] private static partial Regex OrderIdRx();
    [GeneratedRegex("^[A-Za-z0-9_]{1,40}$")] private static partial Regex CampoKeyRx();
    [GeneratedRegex("^[\\x21-\\x7E]{8,128}$")] private static partial Regex IdemKeyRx();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")] private static partial Regex GatewayCodeRx();
    [GeneratedRegex("^[A-Za-z0-9 ]{1,13}$")] private static partial Regex SoftDescriptorRx();

    // Regras do <billing>: campo -> (tamanho máximo, regex). Valor fora da regra = 400 com o NOME do campo, nunca o valor.
    static readonly (string Campo, Func<BillingInput, string?> Get, Regex Rx)[] BillingRules =
    [
        ("customerIdentity", b => b.CustomerIdentity, new("^[0-9A-Za-z./-]{1,20}$")),
        ("name",             b => b.Name,             new(@"^[\p{L}\p{M} .'-]{2,100}$")),
        ("address",          b => b.Address,          new(@"^[\p{L}\p{M}0-9 .,'/º°ª-]{1,120}$")),
        ("address2",         b => b.Address2,         new(@"^[\p{L}\p{M}0-9 .,'/º°ª-]{1,60}$")),
        ("city",             b => b.City,             new(@"^[\p{L}\p{M} .'-]{1,60}$")),
        ("state",            b => b.State,            new("^[A-Z]{2}$")),
        ("postalCode",       b => b.PostalCode,       new("^[0-9]{8}$")),
        ("country",          b => b.Country,          new("^[A-Z]{2}$")),
        ("phone",            b => b.Phone,            new("^[0-9]{10,13}$")),
        ("email",            b => b.Email,            new(@"^[^@\s<>]{1,64}@[^@\s<>]{1,120}\.[A-Za-z]{2,}$")),
    ];

    static IResult Err(int status, string code) => Results.Json(new { erro = code }, statusCode: status);

    // Marca o passo para a linha de rastreio (sai no fim da requisição, com status e duração). Sem dado sensível.
    static IResult M(HttpContext ctx, string passo, Guid? tx, string resultado, IResult r, Guid? localRef = null, string? orderId = null)
    {
        Chk3Trace.Mark(ctx, passo, tx, resultado, localRef, orderId);
        return r;
    }

    // ---------- Passos 1 a 4: POST /sessions (JSON) ----------
    public static async Task<IResult> CreateSession(HttpContext ctx, CreateSessionRequest? req, Store store, IntegrationConfig cfg)
    {
        string idemKey = ctx.Request.Headers["Idempotency-Key"].ToString();
        const string P = "2-4";
        if (!IdemKeyRx().IsMatch(idemKey)) return M(ctx, P, null, "idempotency_key_invalida", Err(400, "idempotency_key_invalida"));
        if (req is null) return M(ctx, P, null, "corpo_invalido", Err(400, "corpo_invalido"));

        // Passo 2: autoriza cliente/merchant (canal = sub do JWT, precisa estar ativo no cadastro).
        var channelId = ctx.User.FindFirst("sub")!.Value;
        var channel = await store.GetChannelAsync(channelId);
        if (channel is null) return M(ctx, P, null, "2_canal_nao_autorizado", Err(403, "canal_nao_autorizado"));

        // Único campo obrigatório: valor total em centavos.
        if (req.Amount is not (> 0 and <= 1_000_000_000)) return M(ctx, P, null, "2_valor_invalido", Err(400, "valor_invalido"));
        if (req.OrderId is not null && (!OrderIdRx().IsMatch(req.OrderId) || Card.ContainsPanPattern(req.OrderId)))
            return M(ctx, P, null, "2_pedido_invalido", Err(400, "pedido_invalido")); // orderId inválido não vai para o log
        var currency = req.Currency ?? "BRL";
        if (currency != "BRL") return M(ctx, P, null, "2_moeda_invalida", Err(400, "moeda_invalida"));

        // 2a: camposAdicionais fora dos limites ou com padrão de PAN. Não registra e não loga o valor.
        var campos = req.CamposAdicionais ?? new();
        if (campos.Count > 10 || campos.Any(kv => !CampoKeyRx().IsMatch(kv.Key) || kv.Value is null
                || kv.Value.Length > 255 || Card.ContainsPanPattern(kv.Value)))
            return M(ctx, P, null, "2a_campos_adicionais_invalidos", Err(400, "campos_adicionais_invalidos"));
        // 2a: billing, urlReturn e softDescriptor (opcionais). Nunca o valor no log nem na resposta.
        if (req.Billing is { } bill)
            foreach (var (campo, get, rx) in BillingRules)
                if (get(bill) is { } v && (!rx.IsMatch(v) || Card.ContainsPanPattern(v)))
                    return M(ctx, P, null, "2a_billing_" + campo + "_invalido", Err(400, "billing_" + campo + "_invalido"));
        if (req.UrlReturn is { } u && (u.Length > 512 || !Uri.TryCreate(u, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http") || Card.ContainsPanPattern(u)))
            return M(ctx, P, null, "2a_url_return_invalida", Err(400, "url_return_invalida"));
        if (req.SoftDescriptor is { } sd && !SoftDescriptorRx().IsMatch(sd))
            return M(ctx, P, null, "2a_soft_descriptor_invalido", Err(400, "soft_descriptor_invalido"));

        // 2b: parcelas = MÁXIMO permitido nesta sessão. Sem valor, vale o máximo do cadastro do canal.
        if (req.Installments is { } n && (n < 1 || n > channel.MaxInstallments)) return M(ctx, P, null, "2b_parcelas_invalidas", Err(400, "parcelas_invalidas"));
        var maxInstallments = req.Installments ?? channel.MaxInstallments;
        var billingJson = req.Billing is { } b && BillingRules.Any(r => r.Get(b) is not null)
            ? JsonSerializer.Serialize(BillingRules.Where(r => r.Get(b) is not null).ToDictionary(r => r.Campo, r => r.Get(b)))
            : null;

        // Passo 3: fingerprint do conteúdo (como enviado) para a admissão idempotente.
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            req.OrderId, req.Amount, req.Currency, req.Installments, Billing = billingJson, req.UrlReturn, req.SoftDescriptor,
            Campos = new SortedDictionary<string, string>(campos, StringComparer.Ordinal),
        }));

        await using var conn = await store.Db.OpenConnectionAsync();
        await using (var tx = await conn.BeginTransactionAsync())
        {
            // Pedido ausente: referência interna aleatória (não é o transactionId).
            var orderId = req.OrderId ?? "CHK3-" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
            // 3c: admissão atômica por canal + chave. ON CONFLICT resolve a corrida entre réplicas.
            var created = await conn.QuerySingleOrDefaultAsync<CreatedRow>("""
                INSERT INTO payment_sessions (transaction_id, channel_id, idempotency_key, fingerprint, order_id,
                    amount_cents, currency, installments, campos_adicionais, brands, max_installments, expires_at,
                    billing, url_return, soft_descriptor)
                VALUES (@id, @channelId, @idemKey, @fingerprint, @orderId, @amount, @currency, @installments,
                    CAST(@campos AS jsonb), @brands, @maxInst, now() + make_interval(secs => @ttl),
                    CAST(@billing AS jsonb), @urlReturn, @softDescriptor)
                ON CONFLICT (channel_id, idempotency_key) DO NOTHING
                RETURNING transaction_id AS Id, expires_at AS ExpiresAt
                """, new
                {
                    id = Guid.CreateVersion7(), channelId, idemKey, fingerprint, orderId,
                    amount = req.Amount, currency, installments = req.Installments,
                    campos = JsonSerializer.Serialize(campos), brands = channel.Brands,
                    maxInst = maxInstallments, ttl = (double)channel.SessionTtlSeconds,
                    billing = billingJson, urlReturn = req.UrlReturn, softDescriptor = req.SoftDescriptor,
                }, tx);

            if (created is { } c)
            {
                var (plain, hash) = Store.NewCredential("pc_");
                await Store.InsertTokenAsync(conn, c.Id, hash, "pagamento", c.ExpiresAt, channel.Brands, maxInstallments, tx);
                await tx.CommitAsync();
                return M(ctx, P, c.Id, "3c_criada", Results.Json(SessionResponse(cfg, c.Id, "Criada", c.ExpiresAt, plain, orderId, maxInstallments), statusCode: 201), orderId: orderId);
            }
            await tx.RollbackAsync();
        }

        // Chave já usada: 3a ou 3b.
        var existing = await conn.QuerySingleAsync<SessionRow>($"""
            SELECT {Store.SessionCols()} FROM payment_sessions p JOIN channels ch ON ch.id = p.channel_id
            WHERE p.channel_id = @channelId AND p.idempotency_key = @idemKey
            """, new { channelId, idemKey });

        if (!CryptographicOperations.FixedTimeEquals(existing.Fingerprint, fingerprint))
            return M(ctx, P, existing.TransactionId, "3a_conteudo_diferente", Err(409, "idempotency_key_com_conteudo_diferente")); // 3a

        // 3b: sessão aberta, no TTL e sem tentativa: nova credencial, mesmo TTL (não estende).
        var hasAttempt = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM attempts WHERE transaction_id = @id)", new { id = existing.TransactionId });
        if (existing.State == "Criada" && !hasAttempt && existing.ExpiresAt > DateTime.UtcNow)
        {
            var (plain, hash) = Store.NewCredential("pc_");
            await Store.InsertTokenAsync(conn, existing.TransactionId, hash, "pagamento", existing.ExpiresAt, existing.Brands, existing.MaxInstallments);
            return M(ctx, P, existing.TransactionId, "3b_nova_credencial", Results.Json(SessionResponse(cfg, existing.TransactionId, existing.State, existing.ExpiresAt, plain, existing.OrderId, existing.MaxInstallments), statusCode: 200), orderId: existing.OrderId);
        }
        var state = existing.State == "Criada" && existing.ExpiresAt <= DateTime.UtcNow ? "Expirada" : existing.State;
        return M(ctx, P, existing.TransactionId, "3b_estado_" + state, Results.Json(SessionResponse(cfg, existing.TransactionId, state, existing.ExpiresAt, null, existing.OrderId, existing.MaxInstallments), statusCode: 200), orderId: existing.OrderId);
    }

    static object SessionResponse(IntegrationConfig cfg, Guid id, string state, DateTime expiresAt, string? credential, string orderId, int maxInstallments) => new
    {
        transactionId = id,
        state,
        orderId,
        maxInstallments,
        expiresAt = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
        // Passo 4: credencial no fragment (nunca na query). O valor nunca vai na URL.
        frameUrl = credential is null ? null : $"{cfg.OpcoesOrigin}/#pc={credential}",
        paymentCredential = credential,
    };

    // ---------- Passo 6: valida credencial de pagamento sem consumir ----------
    public static async Task<IResult> ValidatePaymentCredential(HttpContext ctx, Store store)
    {
        var s = await store.FindOpenSessionByCredentialAsync(ctx.Request.Headers[Web.PaymentCredentialHeader], "pagamento");
        if (s is null) return M(ctx, "6", null, "6a_recusada", Err(401, "recusada")); // 6a sem detalhes
        Chk3Trace.Mark(ctx, "6", s.TransactionId, "valida", orderId: s.OrderId);
        // 6b: dados só para exibição.
        return Results.Ok(new
        {
            transactionId = s.TransactionId,
            merchantName = s.DisplayName,
            methods = new[] { "cartao_credito" }, // Google Pay: desenho separado (fora do POC)
            brands = s.Brands,
            maxInstallments = Installments.MaxFor(s.AmountCents, s.MaxInstallments), // parcelas: até este máximo
            amountCents = s.AmountCents,
            currency = s.Currency,
            orderId = s.OrderId,
        });
    }

    // ---------- Passo 7: revalida e emite credencial de digitação + snapshot assinado ----------
    public static async Task<IResult> IssueTypingCredential(HttpContext ctx, TypingCredentialRequest? req, Store store, SnapshotSigner signer)
    {
        if (req?.Method != "cartao_credito") return M(ctx, "7", null, "meio_invalido", Err(400, "meio_invalido"));
        var s = await store.FindOpenSessionByCredentialAsync(ctx.Request.Headers[Web.PaymentCredentialHeader], "pagamento");
        if (s is null) return M(ctx, "7", null, "recusada", Err(401, "recusada"));

        await using var conn = await store.Db.OpenConnectionAsync();
        var issued = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM frame_tokens WHERE transaction_id = @id AND kind = 'digitacao'", new { id = s.TransactionId });
        if (issued >= 20) return M(ctx, "7", s.TransactionId, "limite_credenciais", Err(429, "limite_credenciais")); // evita crescimento sem limite por sessão

        var (plain, hash) = Store.NewCredential("tc_");
        await Store.InsertTokenAsync(conn, s.TransactionId, hash, "digitacao", s.ExpiresAt, s.Brands, s.MaxInstallments); // expira junto com a sessão
        Chk3Trace.Mark(ctx, "7", s.TransactionId, "emitida");
        return Results.Ok(new
        {
            transactionId = s.TransactionId, // para o rastreio na Tela de opções (não é segredo)
            typingCredential = plain,
            snapshot = signer.Sign(ToSnapshot(s)),
            expiresAt = DateTime.SpecifyKind(s.ExpiresAt, DateTimeKind.Utc),
        });
    }

    static Snapshot ToSnapshot(SessionRow s) => new(s.TransactionId, s.Brands, Installments.MaxFor(s.AmountCents, s.MaxInstallments),
        new DateTimeOffset(DateTime.SpecifyKind(s.ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds());

    // ---------- Passo 8: valida credencial de digitação sem consumir e devolve snapshot ----------
    public static async Task<IResult> ValidateTypingCredential(HttpContext ctx, Store store, SnapshotSigner signer)
    {
        var s = await store.FindOpenSessionByCredentialAsync(ctx.Request.Headers[Web.TypingCredentialHeader], "digitacao");
        if (s is null) return M(ctx, "8", null, "recusada", Err(401, "recusada"));
        Chk3Trace.Mark(ctx, "8", s.TransactionId, "valida");
        // Valor e moeda só para exibição na página de digitação (o navegador nunca envia valor no passo 9).
        return Results.Ok(new
        {
            transactionId = s.TransactionId, snapshot = signer.Sign(ToSnapshot(s)),
            maxInstallments = Installments.MaxFor(s.AmountCents, s.MaxInstallments), brands = s.Brands, amountCents = s.AmountCents, currency = s.Currency,
        });
    }

    // ---------- Passo 10: tentativa única atômica, sem PAN ----------
    public static async Task<IResult> RegisterAttempt(HttpContext ctx, AttemptRequest? req, Store store)
    {
        string? plain = ctx.Request.Headers[Web.TypingCredentialHeader];
        if (req is null || string.IsNullOrEmpty(plain) || plain.Length > 128) return M(ctx, "10", null, "10a_sessao_invalida", Err(422, "sessao_invalida"));

        await using var conn = await store.Db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Lock na sessão: serializa tentativas concorrentes da mesma sessão.
        var s = await conn.QuerySingleOrDefaultAsync<SessionRow>($"""
            SELECT {Store.SessionCols("cr")}
            FROM frame_tokens cr
            JOIN payment_sessions p ON p.transaction_id = cr.transaction_id
            JOIN channels ch ON ch.id = p.channel_id
            WHERE cr.hash = @hash AND cr.kind = 'digitacao'
            FOR UPDATE OF p
            """, new { hash = Store.Hash(plain) }, tx);
        if (s is null) return M(ctx, "10", null, "10a_sessao_invalida", Err(422, "sessao_invalida")); // 10a

        var attempt = await conn.QuerySingleOrDefaultAsync<AttemptRow>(
            "SELECT local_ref AS LocalRef, stage AS Stage, result AS Result FROM attempts WHERE transaction_id = @id",
            new { id = s.TransactionId }, tx);
        if (attempt is not null) // 10b: tentativa repetida, estado conhecido, sem novo envio
            return M(ctx, "10", s.TransactionId, "10b_ja_registrada_" + s.State, Results.Json(new { erro = "tentativa_ja_registrada", state = s.State }, statusCode: 409), attempt.LocalRef);

        var credOk = await conn.ExecuteScalarAsync<bool>(
            "SELECT expires_at > now() FROM frame_tokens WHERE hash = @hash", new { hash = Store.Hash(plain) }, tx);
        // Parcelas: 1 <= n <= máximo da sessão (nunca confia no cliente).
        bool installmentsOk = Installments.IsValid(s.AmountCents, req.Installments, s.MaxInstallments);
        if (!credOk || req.TransactionId != s.TransactionId || s.ExpiresAt <= DateTime.UtcNow || s.State != "Criada"
            || req.Brand is null || !s.Brands.Contains(req.Brand) || !installmentsOk)
            return M(ctx, "10", s.TransactionId, "10a_sessao_invalida", Err(422, "sessao_invalida")); // 10a: sessão inválida/divergente do snapshot, bandeira fora do snapshot ou parcelas inválidas

        // 10c: registra a tentativa única e muda o estado.
        var localRef = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO attempts (transaction_id, local_ref, brand, installments, stage)
            VALUES (@id, @localRef, @brand, @inst, 'envio_iniciado');
            UPDATE payment_sessions SET state = 'AutorizacaoSolicitada', updated_at = now() WHERE transaction_id = @id;
            UPDATE frame_tokens SET used_at = now() WHERE transaction_id = @id AND used_at IS NULL;
            """, new { id = s.TransactionId, localRef, brand = req.Brand, inst = req.Installments }, tx);
        var extras = await conn.QuerySingleAsync<PaymentExtras>(
            "SELECT billing::text AS Billing, url_return AS UrlReturn, soft_descriptor AS SoftDescriptor FROM payment_sessions WHERE transaction_id = @id",
            new { id = s.TransactionId }, tx);
        await tx.CommitAsync();
        Chk3Trace.Mark(ctx, "10", s.TransactionId, "10c_registrada", localRef, s.OrderId);

        // Valores do servidor, nunca do navegador. Dados do comprador seguem para o <billing> do Gateway (sem PAN).
        return Results.Json(new
        {
            transactionId = s.TransactionId, localRef, amountCents = s.AmountCents, currency = s.Currency,
            installments = req.Installments, orderId = s.OrderId,
            billing = extras.Billing is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(extras.Billing),
            urlReturn = extras.UrlReturn, softDescriptor = extras.SoftDescriptor,
        }, statusCode: 201);
    }

    // ---------- Passo 13: registra resultado, evidência sanitizada e outbox atômicos ----------
    public static async Task<IResult> RegisterResult(HttpContext ctx, Guid localRef, ResultRequest? req, Store store)
    {
        if (req?.Result is not ("Autorizada" or "Recusada" or "Indeterminada")) return M(ctx, "13", null, "resultado_invalido", Err(400, "resultado_invalido"), localRef);
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
            var known = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT transaction_id FROM attempts WHERE local_ref = @localRef", new { localRef }, tx);
            return prev == req.Result
                ? M(ctx, "13", known, "ja_registrado_" + prev, Results.Ok(new { state = prev }), localRef)
                : M(ctx, "13", known, "resultado_ja_registrado", Err(409, "resultado_ja_registrado"), localRef);
        }
        await conn.ExecuteAsync("""
            UPDATE payment_sessions SET state = @result, updated_at = now() WHERE transaction_id = @txId;
            INSERT INTO outbox (transaction_id, event_type, payload)
            VALUES (@txId, 'pagamento.' || lower(@result), jsonb_build_object('transactionId', @txId, 'state', @result));
            """, new { txId, result = req.Result }, tx);
        await tx.CommitAsync();
        Chk3Trace.Mark(ctx, "13", txId, req.Result + "_outbox_pagamento." + req.Result.ToLowerInvariant(), localRef);
        return Results.Ok(new { state = req.Result });
    }
}

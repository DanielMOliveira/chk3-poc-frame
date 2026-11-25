using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chk3.Shared;

/// <summary>
/// Rastreio de ponta a ponta. Log JSON (uma linha por evento) e uma linha "chk3" por passo do frame v7, com
/// passo, servico, transactionId, localRef, orderId, resultado, status, duracaoMs e correlationId.
/// O X-Correlation-Id é gerado no canal antes do POST /sessions e passa a ser o transactionId assim que ele é conhecido.
/// Ele é propagado em todas as chamadas entre serviços (handler do HttpClient) e entra no escopo de log.
/// NUNCA registrar: PAN, CVV, validade, nome, credenciais pc_/tc_, fragment, JWT, hash ou snapshot.
/// </summary>
public static partial class Chk3Trace
{
    public const string Header = "X-Correlation-Id";
    const string ItemCid = "chk3.cid", ItemT0 = "chk3.t0", ItemMark = "chk3.mark", ItemLogged = "chk3.logged";
    static readonly AsyncLocal<string?> current = new();
    static ILogger log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public static string Servico { get; private set; } = "?";

    // Só aceita ids simples (GUID ou similar): o valor vem de fora e vai para o log.
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")] private static partial Regex CidRx();

    public static string? Current => current.Value;

    /// <summary>Console JSON com escopos, UTC. As linhas do HttpClient ficam em Warning: o rastro vem das linhas chk3.</summary>
    public static WebApplicationBuilder AddChk3Logging(this WebApplicationBuilder b, string servico)
    {
        Servico = servico;
        b.Logging.ClearProviders();
        b.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });
        b.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        b.Services.ConfigureHttpClientDefaults(h => h.AddHttpMessageHandler(() => new CorrelationHandler()));
        return b;
    }

    /// <summary>Primeiro middleware: correlation id (header válido ou novo), escopo de log e linha de fallback por requisição.</summary>
    public static IApplicationBuilder UseChk3Trace(this WebApplication app)
    {
        log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("chk3");
        return app.Use(async (ctx, next) =>
        {
            string h = ctx.Request.Headers[Header].ToString();
            SetCorrelation(ctx, CidRx().IsMatch(h) ? h : Guid.NewGuid().ToString());
            ctx.Items[ItemT0] = Stopwatch.GetTimestamp();
            using var scope = log.BeginScope(new Scope(current.Value, Servico));
            try { await next(); }
            catch { Write(ctx, "-", null, "excecao", 500, Elapsed(ctx), null, null, Route(ctx)); throw; }
            if (ctx.Items[ItemMark] is MarkData m)
                Write(ctx, m.Passo, m.Tx, m.Resultado, ctx.Response.StatusCode, Elapsed(ctx), m.LocalRef, m.OrderId, null);
            else if (!ctx.Items.ContainsKey(ItemLogged) && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
                Write(ctx, "-", null, "sem_passo", ctx.Response.StatusCode, Elapsed(ctx), null, null, Route(ctx));
        });
    }

    /// <summary>Passa a usar este id (normalmente o transactionId) nas próximas chamadas desta requisição.</summary>
    public static void SetCorrelation(HttpContext ctx, string id) { ctx.Items[ItemCid] = id; current.Value = id; }
    public static void SetCorrelation(HttpContext ctx, Guid tx) => SetCorrelation(ctx, tx.ToString());

    /// <summary>Marca o passo desta requisição. A linha sai no fim, com o status HTTP e a duração total.</summary>
    public static void Mark(HttpContext ctx, string passo, Guid? tx, string resultado, Guid? localRef = null, string? orderId = null)
    {
        if (tx is { } t) SetCorrelation(ctx, t);
        ctx.Items[ItemMark] = new MarkData(passo, tx, resultado, localRef, orderId);
    }

    /// <summary>Registra já um passo (serviços com vários passos na mesma requisição, ex.: autorizacao).</summary>
    public static void Step(HttpContext ctx, string passo, Guid? tx, string resultado, int? status, long duracaoMs, Guid? localRef = null, string? orderId = null)
    {
        if (tx is { } t) SetCorrelation(ctx, t);
        ctx.Items[ItemLogged] = true;
        Write(ctx, passo, tx, resultado, status, duracaoMs, localRef, orderId, null);
    }

    public static long Since(long t0) => (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    static long Elapsed(HttpContext ctx) => ctx.Items[ItemT0] is long t0 ? Since(t0) : 0;
    // Padrão da rota (nunca o path bruto, que vem de fora).
    static string Route(HttpContext ctx) => (ctx.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "nao_mapeada";

    static void Write(HttpContext ctx, string passo, Guid? tx, string resultado, int? status, long ms, Guid? localRef, string? orderId, string? rota) =>
        log.LogInformation(
            "chk3 passo={passo} servico={servico} transactionId={transactionId} localRef={localRef} orderId={orderId} resultado={resultado} status={status} duracaoMs={duracaoMs} correlationId={correlationId} rota={rota}",
            passo, Servico, tx, localRef, orderId, resultado, status, ms, ctx.Items[ItemCid] as string, rota);

    /// <summary>Escopo de log: correlationId recebido (ou gerado) e serviço. ToString legível no JSON.</summary>
    sealed class Scope(string? cid, string servico) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        readonly KeyValuePair<string, object?>[] items = [new("correlationId", cid), new("servico", servico)];
        public KeyValuePair<string, object?> this[int i] => items[i];
        public int Count => items.Length;
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => $"correlationId:{cid} servico:{servico}";
    }

    sealed record MarkData(string Passo, Guid? Tx, string Resultado, Guid? LocalRef, string? OrderId);

    /// <summary>Propaga o correlation id atual em toda chamada HttpClient entre serviços.</summary>
    sealed class CorrelationHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (current.Value is { } cid && !request.Headers.Contains(Header)) request.Headers.TryAddWithoutValidation(Header, cid);
            return base.SendAsync(request, ct);
        }
    }
}

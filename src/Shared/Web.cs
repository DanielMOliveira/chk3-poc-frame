using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Chk3.Shared;

public static class Web
{
    /// <summary>Cabeçalhos de segurança comuns. CSP específico por app.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, string csp) =>
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h["Content-Security-Policy"] = csp;
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "no-referrer";
                h["Cache-Control"] = "no-store";
                h["Cross-Origin-Opener-Policy"] = "same-origin-allow-popups";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            });
            await next();
        });

    /// <summary>Credencial nunca em query string: qualquer query é recusada (v7, nota do passo 4).</summary>
    public static IApplicationBuilder UseRejectQueryString(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.QueryString.HasValue)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { erro = "query_string_nao_permitida" });
                return;
            }
            await next();
        });

    public static bool FixedTimeEquals(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public const string InternalKeyHeader = "X-Internal-Key";
    /// <summary>Header da credencial de pagamento. Nome a definir (v7 §4). POC usa este.</summary>
    public const string PaymentCredentialHeader = "X-Payment-Credential";
    /// <summary>Header da credencial de digitação. Nome a definir (v7 §4).</summary>
    public const string TypingCredentialHeader = "X-Typing-Credential";
}

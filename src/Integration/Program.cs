using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Chk3.Integration;
using Chk3.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddChk3Logging("integration");

// Duas portas: pública (só POST /sessions, publicada pelo compose) e interna (endpoints do passo 6 a 13,
// só na rede interna do compose). No desenho v7 a interna seria private endpoint + MI.
int publicPort = int.Parse(Env.Optional("INTEGRATION_PUBLIC_PORT", "8080"));
int internalPort = int.Parse(Env.Optional("INTEGRATION_INTERNAL_PORT", "8090"));
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(publicPort);
    k.ListenAnyIP(internalPort);
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024;
});

var cfg = new IntegrationConfig(
    OpcoesOrigin: Env.Required("OPCOES_PUBLIC_ORIGIN").TrimEnd('/'),
    InternalKey: Env.Required("INTERNAL_API_KEY"));

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton(NpgsqlDataSource.Create(Env.Required("POSTGRES_CONNECTION")));
builder.Services.AddSingleton(new SnapshotSigner(Env.Secret("SNAPSHOT_SIGNING_KEY")));
builder.Services.AddSingleton<Store>();

// Token estático do canal (substitui Identity Server + APIM no POC). HS256, segredo do .env.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = Env.Required("CHANNEL_JWT_ISSUER"),
        ValidateAudience = true, ValidAudience = "chk3-integration",
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        RequireExpirationTime = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        IssuerSigningKey = new SymmetricSecurityKey(Env.Secret("CHANNEL_JWT_SECRET")),
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("sessions", p =>
    p.RequireAuthenticatedUser().RequireClaim("sub").RequireAssertion(c =>
        (c.User.FindFirst("scope")?.Value ?? "").Split(' ').Contains("sessions:create"))));

// Rate limit por canal (no v7 fica no APIM).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("canal", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseChk3Trace();
await Migrator.RunAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.UseSecurityHeaders("default-src 'none'; frame-ancestors 'none'");
app.UseRejectQueryString();

// Separação de portas: /internal só na porta interna e com chave; /sessions só na pública.
app.Use(async (ctx, next) =>
{
    var isInternalPath = ctx.Request.Path.StartsWithSegments("/internal");
    var onInternalPort = ctx.Connection.LocalPort == internalPort;
    if (ctx.Request.Path != "/health" && isInternalPath != onInternalPort)
    {
        ctx.Response.StatusCode = 404; return;
    }
    if (isInternalPath && !Web.FixedTimeEquals(ctx.Request.Headers[Web.InternalKeyHeader], cfg.InternalKey))
    {
        ctx.Response.StatusCode = 401; return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Passos 1 a 4
app.MapPost("/sessions", Endpoints.CreateSession).RequireAuthorization("sessions").RequireRateLimiting("canal");

var internalApi = app.MapGroup("/internal/v1");
internalApi.MapPost("/payment-credential/validate", Endpoints.ValidatePaymentCredential); // passo 6
internalApi.MapPost("/typing-credential", Endpoints.IssueTypingCredential);               // passo 7
internalApi.MapPost("/typing-credential/validate", Endpoints.ValidateTypingCredential);   // passo 8
internalApi.MapPost("/attempts", Endpoints.RegisterAttempt);                              // passo 10
internalApi.MapPost("/attempts/{localRef:guid}/result", Endpoints.RegisterResult);        // passo 13

app.Run();

public sealed record IntegrationConfig(string OpcoesOrigin, string InternalKey);

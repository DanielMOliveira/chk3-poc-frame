using System.Net;
using System.Text.Json;
using System.Net.Http.Json;

namespace Chk3.Tests;

/// <summary>Testes de integração contra a stack do docker compose (HTTP real).</summary>
public class FrameFlowTests : IDisposable
{
    const string Approve = "4111111111111111", Decline = "4000000000000002", Timeout = "4000000000000119";
    readonly Flow f = new();
    public void Dispose() => f.Dispose();

    // ---------- Idempotência (passo 3) ----------
    [Fact]
    public async Task Idempotencia_replay_mesma_sessao_com_credencial_nova_e_mesmo_ttl()
    {
        var key = Flow.NewKey();
        var a = await f.CreateSession(Flow.Body("PED-IDEM"), key);
        var b = await f.CreateSession(Flow.Body("PED-IDEM"), key);
        Assert.Equal(201, a.Status);
        Assert.Equal(200, b.Status);                         // 3b
        Assert.Equal(a.TransactionId, b.TransactionId);
        Assert.NotEqual(a.PaymentCredential, b.PaymentCredential);
        // As duas credenciais valem até o TTL (v7 §3.1: anteriores continuam válidas).
        Assert.Equal(HttpStatusCode.OK, (await f.Step6(a.PaymentCredential!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Step6(b.PaymentCredential!)).StatusCode);
    }

    [Fact]
    public async Task Idempotencia_mesma_chave_conteudo_diferente_409()
    {
        var key = Flow.NewKey();
        Assert.Equal(201, (await f.CreateSession(Flow.Body("PED-X", 1000), key)).Status);
        using var r = await f.PostSessionRaw(Flow.Body("PED-X", 2000), key);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);  // 3a
    }

    [Fact]
    public async Task Idempotencia_concorrente_cria_uma_unica_sessao()
    {
        var key = Flow.NewKey();
        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.CreateSession(Flow.Body("PED-CONC"), key)));
        Assert.Single(all, s => s.Status == 201);
        Assert.Single(all.Select(s => s.TransactionId).Distinct());
    }

    // ---------- Credenciais (passos 4-8) ----------
    [Fact]
    public async Task Frame_url_tem_credencial_no_fragment_e_nunca_na_query()
    {
        var s = await f.CreateSession();
        var uri = new Uri(s.FrameUrl!);
        Assert.Equal("", uri.Query);
        Assert.StartsWith("#pc=pc_", uri.Fragment);
        Assert.DoesNotContain("1000", s.FrameUrl);           // valor nunca na URL
    }

    [Fact]
    public async Task Credencial_invalida_recusada()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Step6("pc_inexistente")).StatusCode);
        Assert.Equal(401, (await f.Step7("pc_inexistente")).Status);
        Assert.Equal(401, (await f.Step8("tc_inexistente")).Status);
        // Credencial de digitação não serve como credencial de pagamento e vice-versa.
        var (s, tc, _) = await f.UpToStep8();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Step6(tc)).StatusCode);
        Assert.Equal(401, (await f.Step8(s.PaymentCredential!)).Status);
    }

    [Fact]
    public async Task Credencial_reapresentavel_dentro_do_ttl()
    {
        var s = await f.CreateSession();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await f.Step6(s.PaymentCredential!)).StatusCode);
        var (_, tc) = await f.Step7(s.PaymentCredential!);
        for (var i = 0; i < 2; i++) Assert.Equal(200, (await f.Step8(tc!)).Status);
    }

    [Fact]
    public async Task Credencial_expirada_recusada_e_3b_nao_reemite()
    {
        // canal-ttl-curto tem TTL de 3 s no cadastro.
        var key = Flow.NewKey();
        var s = await f.CreateSession(key: key, channel: "canal-ttl-curto");
        Assert.Equal(201, s.Status);
        var (_, tc) = await f.Step7(s.PaymentCredential!);
        var (_, snap) = await f.Step8(tc!);
        await Task.Delay(4500);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Step6(s.PaymentCredential!)).StatusCode); // 6a
        Assert.Equal(401, (await f.Step8(tc!)).Status);
        var (st, res) = await f.Step9(tc!, snap!, Approve);                                             // 9a (snapshot expirado)
        Assert.Equal((422, "ValidacaoFalhou"), (st, res));
        var again = await f.CreateSession(key: key, channel: "canal-ttl-curto");
        Assert.Equal((200, "Expirada", (string?)null), (again.Status, again.State, again.FrameUrl));
    }

    [Theory]
    [InlineData("/?pc=pc_abc")]
    [InlineData("/api/session?pc=pc_abc")]
    public async Task Credencial_na_query_string_recusada_na_tela_de_opcoes(string path)
    {
        var s = await f.CreateSession();
        var url = TestEnv.Opcoes + path.Replace("pc_abc", s.PaymentCredential);
        using var r = path.StartsWith("/api") ? await f.Http.PostAsync(url, null) : await f.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Credencial_na_query_string_recusada_no_endpoint_pay_e_na_integration()
    {
        var (_, tc, _) = await f.UpToStep8();
        using var r1 = await f.Http.PostAsync($"{TestEnv.Cartao}/digitacao?tc={tc}", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);
        using var r2 = await f.PostSessionRaw(Flow.Body(), path: "/sessions?x=1");
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    // ---------- Token do canal e validações do passo 2 ----------
    [Fact]
    public async Task Sessions_exige_jwt_valido_do_canal()
    {
        using var semToken = await f.PostSessionRaw(Flow.Body(), jwt: "");
        using var outroSegredo = await f.PostSessionRaw(Flow.Body(), jwt: Flow.Jwt(secret: new string('x', 48)));
        using var expirado = await f.PostSessionRaw(Flow.Body(), jwt: Flow.Jwt(exp: DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()));
        using var semEscopo = await f.PostSessionRaw(Flow.Body(), jwt: Flow.Jwt(scope: "outro"));
        using var canalDesconhecido = await f.PostSessionRaw(Flow.Body(), jwt: Flow.Jwt(channel: "canal-inexistente"));
        Assert.Equal(HttpStatusCode.Unauthorized, semToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, outroSegredo.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expirado.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, semEscopo.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, canalDesconhecido.StatusCode);
    }

    [Fact]
    public async Task Passo2_recusa_pan_em_campos_adicionais_e_parcelas_acima_do_maximo()
    {
        using var pan = await f.PostSessionRaw(Flow.Body(campos: new() { ["obs"] = "4111 1111 1111 1111" }));
        using var parcelas = await f.PostSessionRaw(Flow.Body(installments: 99));
        Assert.Equal(HttpStatusCode.BadRequest, pan.StatusCode);       // 2a
        Assert.Equal(HttpStatusCode.BadRequest, parcelas.StatusCode);  // 2b
        Assert.DoesNotContain("4111", await pan.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Endpoints_internos_nao_respondem_na_porta_publica()
    {
        using var r = await f.Http.PostAsync(TestEnv.Integration + "/internal/v1/payment-credential/validate", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Cabecalhos_de_seguranca_e_frame_ancestors()
    {
        using var op = await f.Http.GetAsync(TestEnv.Opcoes + "/");
        var csp = op.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains($"frame-ancestors {TestEnv.Get("CANAL_PUBLIC_ORIGIN")}", csp);
        Assert.Equal("no-store", op.Headers.CacheControl!.ToString());
        var (_, tc, _) = await f.UpToStep8();
        using var pay = await f.Http.PostAsync(TestEnv.Cartao + "/digitacao", new FormUrlEncodedContent(new Dictionary<string, string> { ["tc"] = tc }));
        Assert.Contains($"frame-ancestors {TestEnv.Opcoes} {TestEnv.Get("CANAL_PUBLIC_ORIGIN")}", pay.Headers.GetValues("Content-Security-Policy").Single());
    }

    // ---------- Passos 9-14 ----------
    [Theory]
    [InlineData(Approve, "Autorizada")]
    [InlineData("5555555555554444", "Autorizada")]
    [InlineData(Decline, "Recusada")]
    [InlineData(Timeout, "Indeterminada")]
    public async Task Gateway_mock_resultado_por_cartao_de_teste(string pan, string expected)
    {
        var (_, tc, snap) = await f.UpToStep8();
        Assert.Equal((200, expected), await f.Step9(tc, snap, pan));
    }

    [Fact]
    public async Task Tentativa_unica_por_sessao_mesmo_concorrente()
    {
        var (_, tc, snap) = await f.UpToStep8();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => f.Step9(tc, snap, Approve)));
        Assert.Single(results, r => r.Status == 200 && r.Result == "Autorizada");
        Assert.Equal(4, results.Count(r => r.Status == 409 && r.Result == "TentativaJaRegistrada")); // 10b
    }

    [Fact]
    public async Task Tentativa_unica_vale_para_todas_as_credenciais_da_sessao()
    {
        var (s, tc1, snap) = await f.UpToStep8();
        var (_, tc2) = await f.Step7(s.PaymentCredential!);   // segunda credencial de digitação, mesma sessão
        Assert.Equal((200, "Recusada"), await f.Step9(tc1, snap, Decline));
        Assert.Equal((409, "TentativaJaRegistrada"), await f.Step9(tc2!, snap, Approve));
        Assert.Equal(401, (await f.Step7(s.PaymentCredential!)).Status);  // sessão concluída: 6a/7
    }

    [Fact]
    public async Task Validacao_9a_nao_registra_tentativa()
    {
        var (_, tc, snap) = await f.UpToStep8();
        Assert.Equal((422, "ValidacaoFalhou"), await f.Step9(tc, snap, "4111111111111112"));      // Luhn
        Assert.Equal((422, "ValidacaoFalhou"), await f.Step9(tc, snap, "4539578763621486"));      // PAN fora da allowlist de teste
        Assert.Equal((422, "ValidacaoFalhou"), await f.Step9(tc, snap, Approve, cvv: "12"));      // CVV
        Assert.Equal((422, "ValidacaoFalhou"), await f.Step9(tc, snap, Approve, installments: 7)); // parcelas > máximo
        Assert.Equal((200, "Autorizada"), await f.Step9(tc, snap, Approve));                      // ainda pode tentar
    }

    [Fact]
    public async Task Bandeira_fora_do_snapshot_recusada()
    {
        var (_, tc, snap) = await f.UpToStep8(channel: "canal-ttl-curto"); // só visa
        Assert.Equal((422, "ValidacaoFalhou"), await f.Step9(tc, snap, "5555555555554444"));
    }

    [Fact]
    public async Task Snapshot_de_outra_sessao_recusado_sem_envio()
    {
        var (_, tcA, _) = await f.UpToStep8();
        var (_, _, snapB) = await f.UpToStep8();
        Assert.Equal((422, "SessaoInvalida"), await f.Step9(tcA, snapB, Approve)); // 10a
        var (_, snapA) = await f.Step8(tcA);
        Assert.Equal((200, "Autorizada"), await f.Step9(tcA, snapA!, Approve));   // sessão A não foi consumida
    }

    [Fact]
    public async Task Limite_de_falhas_de_validacao_por_credencial()
    {
        var (_, tc, snap) = await f.UpToStep8();
        for (var i = 0; i < 5; i++) await f.Step9(tc, snap, "4111111111111112");
        Assert.Equal((429, "LimiteDeTentativas"), await f.Step9(tc, snap, Approve));
    }
}

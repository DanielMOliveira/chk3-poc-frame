# Sequência da POC Frame v7: teste rastreado na VPS e alinhamento com o v7

- **Diagrama:** `seq-poc-frame-v7-teste.mmd` / `.png` (nesta pasta).
- **Desenho original:** `frame-v0-2026-11-24-v7.mmd` / `.png` (pasta `v0-ajustes`, fora do repo).
- **Teste:** transação aprovada `01a21473-ba9f-776d-95c2-a381ed0d3519` (PED-1001, R$ 249,90, visa 1x), rodada na VPS em 25/11/2026 às 20:44 (Lisboa, UTC+0). É o mesmo fluxo que o `scripts/logs-check.sh` e o `scripts/browser-e2e.mjs` exercitam. Os ramos de recusa (`01a21473-d968-…`) e de timeout (`01a21473-f707-…`) vieram da mesma rodada.
- **Evidência:** linhas `chk3` do `docker compose logs | grep <transactionId>`, guardadas em `logs-e-rastreio.md` (commit `d38c6c2`). Os deploys seguintes recriaram os containers, então hoje o grep na VPS devolve 0 linhas para esse id. O estado no Postgres continua lá e foi consultado de novo em 25/11 às 21:4x (Lisboa).
- **Participantes:** os mesmos nomes e a mesma ordem do v7. Identity Server, Front Door, APIM, Firewall e Worker não existem na POC e ficaram fora do diagrama.
- **Numeração:** a mesma do v7 (passos 1 a 14). Os passos 0, 15 e 16 estão fora do escopo da POC.

## Estado no banco do teste (consulta de 25/11, VPS)
```
payment_sessions: state=Autorizada  created_at=20:44:18.464  expires_at=21:14:18.464 (TTL 30 min)  installments=(nulo)
attempts:         local_ref=e68af5d8-…  brand=visa  installments=1  stage=resultado_registrado  result=Autorizada  gateway_code=00
                  created_at=20:44:24.442  updated_at=20:44:24.456
outbox:           id=35  event_type=pagamento.autorizada  created_at=20:44:24.456 (igual ao updated_at da sessão e da tentativa)  delivered_at=(nulo)
credentials:      pagamento 20:44:18.464 e digitacao 20:44:22.373, as duas com expires_at=21:14:18.464
```
O `created_at` do outbox é igual ao `updated_at` da sessão e da tentativa. Isso confirma que o passo 13 grava tudo numa transação só: no Postgres, `now()` é o mesmo dentro de uma transação.

## Tabela de alinhamento

Legenda: **igual** = faz o que o v7 diz; **desvio** = faz diferente ou com substituto; **não implementado** = não existe na POC.

| Passo | O que o v7 diz | O que a POC faz | Linha de log / evidência | Status |
|---|---|---|---|---|
| 0 | O Canal pede token `client_credentials` ao Identity Server | O canal fake assina o próprio JWT HS256 com segredo compartilhado (`CanalFake/Program.cs`) | Sem log: não há chamada | **desvio** |
| 1 | Workflow → Front Door (WAF) → APIM: valida JWT, escopo, rate limit e cotas | O navegador faz `POST /checkout` no canal fake, que chama `POST /sessions` direto na Integration com Bearer, `Idempotency-Key` e `X-Correlation-Id` (gerado). Sem WAF nem APIM. JWT, escopo e rate limit (120/min por canal) ficam na própria Integration | `canal passo=1-4 … resultado=sessao_Criada_frame_embutido_http201 status=200 duracaoMs=11` | **desvio** (sem FD/APIM) |
| 2 | Autoriza cliente e merchant e valida `camposAdicionais` | O canal vem do `sub` do JWT e precisa estar ativo. Valida pedido, valor, moeda, `camposAdicionais` (limites e padrão de PAN) e parcelas | `integration passo=2-4 … resultado=3c_criada status=201` | **igual** |
| 2a / 2b | 400 sem registrar e sem logar o valor / 400 parcelas | `2a_campos_adicionais_invalidos` e `2b_parcelas_invalidas`, ambos 400, sem gravar | Código (`Endpoints.cs`) e testes. Não exercitado neste teste | **igual** (só no código e nos testes) |
| 3 | Lê o cadastro do canal (com TTL) e faz a admissão atômica com fingerprint | `INSERT … ON CONFLICT (channel_id, idempotency_key)` com fingerprint SHA-256. Cadastro com TTL, bandeiras e parcelas máximas | `3c_criada`. No banco: `expires_at` = criação + 30 min | **igual** |
| 3a | 409 conflito sem nova sessão | 409 `idempotency_key_com_conteudo_diferente` | Código e testes. Não exercitado neste teste | **igual** |
| 3b | Sessão aberta: emite de novo a credencial com o mesmo TTL. Senão devolve o estado atual | Faz isso. As credenciais anteriores **continuam válidas até o TTL** | Código (`3b_nova_credencial` / `3b_estado_*`) e botão "Reabrir" do canal. Não exercitado neste teste | **igual à premissa do v7**. Pendência v7 §4 aberta: revogar ou não |
| 3c | Cria pagamento e sessão com snapshot do cadastro. Grava só o hash da credencial | Uma linha em `payment_sessions` (pagamento e sessão juntos), com cópia das bandeiras, parcelas máx. e TTL. Hash em `credentials`, na mesma transação | `3c_criada`. No banco: `credentials pagamento` com o mesmo `created_at` da sessão | **igual** (pagamento e sessão numa tabela só) |
| 4 | URL da tela, transactionId e credencial. Abre o iframe com `allow=payment`. Credencial por POST ou fragment, nunca na query | A credencial vai no fragment (`#pc=`). O canal renderiza o iframe com `allow="payment"`. O valor não vai na URL | `canal passo=1-4 … http201`. A correlation troca do id gerado para o transactionId | **igual** |
| 5 | Carrega a tela e entrega a credencial. O JS guarda só em memória e envia num header, sem cookie | O JS lê o fragment, guarda em memória e limpa a URL (`history.replaceState`). Envia `X-Payment-Credential` | **Sem linha de log**: o passo 5 fica só no navegador (o GET estático não é marcado) | **igual**, sem evidência no servidor |
| 5 (reapresentação) | Recarregar o iframe funciona dentro do TTL | O botão "Recarregar frame" do canal funciona porque reaplica a URL com o fragment. **F5 dentro do iframe perde a credencial** (a URL já foi limpa) e mostra erro | Código (`opcoes.js`) | **desvio** |
| 6 | Valida sem consumir: existe, no TTL, do canal, sessão sem tentativa concluída | Valida por hash, TTL da credencial e da sessão, canal ativo, `state='Criada'` e **nenhuma** tentativa (`Store.FindOpenSessionByCredentialAsync`) | `opcoes passo=6 … resultado=valida status=200` (2x: 20:44:19.975 e 20:44:21.732) + `integration passo=6 … valida` | **desvio parcial**: fecha com qualquer tentativa, mesmo em curso, não só "concluída" |
| 6a | Recusa sem detalhes. Página de erro, o canal pede novo frame | 401 sem detalhes. A tela mostra erro e oferece voltar à loja | Código e E2E (caso de credencial inválida). Não exercitado neste teste | **igual** |
| 6b | Meios, bandeiras, marca, mensagens, parcelas e resumo. CSP `frame-ancestors` do canal | Devolve meios, bandeiras, marca, parcelas máx. e resumo. CSP `frame-ancestors {canal}`. Pix e Boleto aparecem como "Em breve". Google Pay não existe | `passo=6 … valida` | **igual** (Google Pay fora) |
| 7 | Escolhe cartão **e parcelas**. Revalida a credencial de pagamento e emite a de digitação (hash, expira com a sessão) e o snapshot assinado | Revalida e emite a credencial de digitação (hash, `expires_at` = o da sessão) e o snapshot HMAC. **As parcelas são escolhidas depois, no iframe do cartão (passo 9).** O snapshot do passo 7 chega à tela, mas ela não o usa: o cartão pega o do passo 8 | `opcoes passo=7 … credencial_digitacao_emitida` + `integration passo=7 … emitida`. No banco: `credentials digitacao` com o mesmo `expires_at` | **desvio** (lugar das parcelas) |
| 8 | Carrega a digitação com a credencial por POST, via Firewall com MI. Valida sem consumir e devolve o snapshot. Coleta o `deviceSessionId` | Form POST com `tc` e `cid`. O cartão valida na Integration com `X-Internal-Key` (no lugar de MI), sem Firewall. Um antifraude **fake** gera o `deviceSessionId` | `cartao passo=8 … pagina_digitacao_entregue` + `integration passo=8 … valida` | **desvio** (sem FW e MI, antifraude fake) |
| 9 | UX de Luhn, CVV e bandeira. Envia cartão, credencial no header, parcelas, snapshot e `deviceSessionId`, nunca o valor. A Autorização verifica assinatura, sessão e expiração e revalida tudo | O navegador checa Luhn e validade (CVV e bandeira não). Envia `X-Typing-Credential` e `X-Correlation-Id`, sem valor. A Autorização verifica o HMAC e a expiração do snapshot, Luhn, validade, CVV (3 dígitos), bandeira, parcelas e a **allowlist de PAN de teste** | `autorizacao passo=9 … validacao_ok` + `cartao passo=9-14 … Autorizada` | **igual**, com restrições de POC (allowlist, CVV de 3 dígitos, sem Amex) |
| 9a | Descarta o PAN, erro sem dados do cartão, pede correção, sem tentativa | 422 `ValidacaoFalhou` com o código do erro. O PAN é anulado. A tela volta ao formulário. Limite de 5 falhas por credencial de digitação (memória local do cartão, 1 réplica) | Código e testes. Não exercitado neste teste | **igual** |
| 10 | Tentativa única atômica, sem PAN, via Firewall com MI. Revalida sessão, bandeira e parcelas | `POST /internal/v1/attempts` com `X-Typing-Credential`, `X-Internal-Key` e correlation. Transação com `FOR UPDATE` | `autorizacao passo=10 … 10c_registrada status=201` + `integration passo=10 … 10c_registrada` | **igual** (sem FW e MI) |
| 10a / 10b | Recusa sem envio / estado conhecido sem novo envio. Descarta o PAN | 422 `SessaoInvalida` / 409 `TentativaJaRegistrada`. O PAN é anulado | Código e testes. Não exercitado neste teste | **igual** |
| 10c | `AutorizacaoSolicitada` com referência local e etapa "envio iniciado". Devolve valor, moeda, parcelas, pedido e localRef | Tentativa `envio_iniciado` + estado `AutorizacaoSolicitada`. Devolve valor, moeda, parcelas, pedido e localRef | `10c_registrada localRef=e68af5d8-… orderId=PED-1001`. No banco: tentativa criada às 20:44:24.442 | **igual** |
| 11 | Autorização com valor do servidor, parcelas, PAN e `deviceSessionId`, via Firewall e VPN S2S | `POST /poc/v1/authorize` no gateway-mock (rede docker `vpn`, sem Firewall e sem VPN real) com `X-Gateway-Key`. **Contrato inventado** (TODO v7 §4) | `gateway-mock passo=11-12 … approved_00 status=200` | **desvio** (contrato e rede) |
| 12a | Resultado. Dado de cartão na resposta a confirmar | `approved`/`00` ou `declined`/`05`, sem dado de cartão. Uma resposta inesperada vira Indeterminada | `autorizacao passo=11-12 … Autorizada_codigo_00`. Recusa: `declined_05` / `Recusada_codigo_05` | **igual** (mock) |
| 12b | Timeout: indeterminado, sem reenvio | Timeout de 3 s → `Indeterminada`, sem reenvio, com Warning | `autorizacao passo=11-12 … Indeterminada_timeout_12b duracaoMs=3002`. Gateway: `timeout_simulado_cliente_desistiu status=499` | **igual** |
| 12 | Descarta o PAN da memória | `pan = null` no `finally`. Limite: a string do .NET é imutável e a memória só é liberada pelo GC | Código (`Autorizacao/Program.cs`) | **igual**, com essa limitação |
| 13 | Resultado sem PAN via Firewall. Estado, evidência sanitizada, projeção e outbox atômicos | Uma transação: tentativa → `resultado_registrado` + `gateway_code`, estado da sessão e insert no `outbox`. A Autorização tenta até 3 vezes. Se as 3 falharem, devolve `Indeterminada` ao navegador e loga Error, e a tentativa fica em `envio_iniciado` sem Worker para reconciliar. Não há projeção separada: a própria `payment_sessions` faz esse papel | `integration passo=13 … Autorizada_outbox_pagamento.autorizada` + `autorizacao passo=13 … Autorizada_registrado`. No banco: mesmo timestamp em sessão, tentativa e outbox | **igual** (atômico). **Desvio**: falha de registro sem reconciliação |
| 14 | Resultado ao cartão. postMessage com origem restrita à tela. A tela mostra mensagem conclusiva ou inconclusiva ou redirect, `no-store` | A Autorização devolve status e transactionId. O cartão faz postMessage para a tela (origem restrita). A tela mostra o resultado e repassa `chk3.result` ao canal (origem restrita). Não faz redirect. `Cache-Control: no-store` em todas as respostas (`Shared/Web.cs`, `UseSecurityHeaders`) | `cartao passo=9-14 … Autorizada status=200 duracaoMs=23`. **O postMessage fica só no navegador** e não gera log | **igual**, sem evidência do lado do navegador |
| 14 (fecho) | Uma tentativa por sessão. Credenciais válidas até o TTL | **O frame fecha em qualquer tentativa registrada**, inclusive recusada: o passo 6 recusa, e o passo 10 devolve 10b. Pagar de novo exige nova sessão | Código (`state='Criada'` e `NOT EXISTS attempts`) | **desvio** (conhecido) |
| Correlation | Não está no v7 | `X-Correlation-Id` em todas as chamadas. O canal gera o id e, depois do passo 4, ele vira o transactionId. **O navegador envia o transactionId como correlation** a partir do passo 7 (e `cid` no form do passo 8) | Todas as linhas trazem `correlationId=01a21473-ba9f-…` | **desvio** (acréscimo da POC) |
| 15 | O Worker processa indeterminadas, inbox e outbox | Não existe | `outbox.delivered_at` nulo | **não implementado** (fora do escopo) |
| 16 | Webhook ao canal com eventId e assinatura | Não existe. O canal recebe só o postMessage e avisa que ele não prova pagamento | — | **não implementado** (fora do escopo) |

## Desvios além dos conhecidos
Os conhecidos se confirmaram: frame fecha em qualquer tentativa, F5 perde a credencial, contrato do mock inventado, 3b mantém as credenciais antigas até o TTL, passos 15 e 16 fora do escopo, passos 5 e 14 só no navegador, navegador manda o transactionId como correlation. Achei mais estes, todos no código:
1. **Passo 0:** sem Identity Server. O canal assina o próprio JWT HS256 com segredo compartilhado.
2. **Passos 1 e 2:** sem Front Door e sem APIM. JWT, escopo e rate limit ficam dentro da Integration.
3. **Passos 6 a 13:** sem Firewall e sem MI. A chamada interna usa `X-Internal-Key` (segredo compartilhado), numa porta interna separada (8090).
4. **Passo 7:** as parcelas são escolhidas no iframe do cartão, não na tela de opções. O snapshot devolvido no 7 não é usado pela tela.
5. **Passo 6:** "sem tentativa concluída" no v7 virou "sem nenhuma tentativa" na POC. Uma tentativa em curso (`AutorizacaoSolicitada`) também fecha a sessão.
6. **Passo 8:** o antifraude é um stand-in local (`antifraude-fake.js`). O `deviceSessionId` não é logado.
7. **Passo 9:** allowlist de PAN de teste (`POC_TEST_PANS_ONLY=true`), CVV de 3 dígitos (sem Amex). No navegador, a UX checa só Luhn e validade.
8. **Passo 13:** se o registro falhar nas 3 tentativas, o navegador recebe `Indeterminada` e a tentativa fica em `envio_iniciado`. Sem Worker, ninguém reconcilia.
9. **Passo 3c:** pagamento, sessão e projeção ficam numa tabela só (`payment_sessions`).
10. **Passo 14:** sem redirect. A tela repassa o resultado ao canal por postMessage.

## O que não está nem no log nem no código
- Que o v7 tenha um correlation id: não tem. É um acréscimo da POC.
- A rede real (Front Door, Private Link, Firewall, VPN S2S): não existe na POC e não há nada para comprovar.

# Operação: containers, logs e rastreio de uma transação (POC CHK3 frame v7)

Fontes: `docker-compose.yml`, o código em `src/` e `src/Shared/Trace.cs`.
Os comandos e os exemplos foram executados na VPS, no diretório do app, em 25/11/2026.
- Exemplos capturados no commit `37b3f18`. O `189e03d` só deixou legível o texto do escopo; os comandos foram testados de novo nele.
- As transações foram:
  - três feitas no navegador (E2E pelo túnel SSH): aprovada, recusada e indeterminada;
  - três do `scripts/logs-check.sh`.

> **Atualização 27/11/2026:** novo serviço `worker` (passos 15 e 16), XML do Gateway no passo 11, tabela `credentials` renomeada para `frame_tokens` e logs guardados no Loki/Grafana. Ver a seção "Atualização 27/11/2026" no fim. O texto abaixo foi mantido como estava em 25/11.

O `Timestamp` dos logs e os horários do Postgres estão em UTC. Em novembro, Lisboa também está em UTC+0.

```bash
cd /home/daniel/projetos/chk3-poc-frame   # VPS. Em outra máquina, rode na raiz do clone.
```

## Resumo

- **O id que liga tudo é o `transactionId`** (UUIDv7, `payment_sessions.transaction_id`), devolvido no `POST /sessions`.
- **`docker compose logs | grep <transactionId>` mostra todos os passos da transação, nos 6 containers de aplicação**:
  canal, opcoes, cartao, integration, autorizacao e gateway-mock.
  - Na transação aprovada do navegador foram 18 linhas.
  - O `postgres` não loga transações; o estado está nas tabelas (seção "postgres").
- Cada serviço escreve **log JSON, uma linha por evento**: console JSON do .NET, com escopos e `Timestamp` UTC.
- Cada passo do Frame v7 gera **uma linha `chk3`** (`Category: "chk3"`). Campos em `State`:

  | Campo | Conteúdo |
  |---|---|
  | `passo` | passo do v7 (`1-4`, `2-4`, `6`, `7`, `8`, `9`, `10`, `11-12`, `13`, `9-14`), ou `-` (requisição sem passo) |
  | `servico` | canal, opcoes, cartao, integration, autorizacao, gateway-mock |
  | `transactionId` | id da transação (null só quando ainda é desconhecido, ex.: credencial inválida) |
  | `localRef` | referência da tentativa no Gateway, do passo 10 em diante |
  | `orderId` | pedido do canal (quando útil e já validado pela Integration) |
  | `resultado` | código curto do que aconteceu (ex.: `3c_criada`, `6a_recusada`, `10b_ja_registrada_Autorizada`, `Autorizada_codigo_00`, `9a_cartao_invalido`) |
  | `status` | status HTTP da resposta, ou da chamada no caso do autorizacao |
  | `duracaoMs` | duração da requisição ou do passo |
  | `correlationId` | id de correlação vigente (o transactionId, assim que conhecido) |
  | `rota` | só nas linhas `passo=-`: o padrão da rota, nunca o path bruto |

- **`X-Correlation-Id`** (header entre serviços, propagado por um handler do HttpClient):

  | Trecho | Valor do header |
  |---|---|
  | canal → integration (`POST /sessions`) | id **gerado no canal** (o transactionId ainda não existe) |
  | navegador → opcoes → integration, passo 6 | id gerado na opcoes (o navegador só tem a credencial `pc`) |
  | navegador → opcoes → integration, passo 7 | **transactionId** (o JS da Tela envia, a partir do passo 6) |
  | navegador → cartao, passo 8 | navegação do iframe (POST de formulário, não leva header). O transactionId vai no campo `cid` e a cartao o usa nas chamadas dela |
  | cartao → integration (8), navegador → cartao (9), cartao → autorizacao → integration / gateway-mock (9 a 13) | **transactionId** |

  - O id **recebido** fica no escopo do log (`Scopes[].correlationId`).
  - O campo `State.correlationId` mostra o id vigente quando a linha foi escrita: o transactionId, assim que o serviço o conhece.
  - O transactionId não é segredo (aparece na tela do canal). Mesmo assim, o header só serve para correlação: é validado (GUID / `[A-Za-z0-9-]{8,64}`) e nunca autoriza nada.
- **O que nunca é registrado:** PAN, CVV, validade, nome, credenciais `pc_`/`tc_`, fragment `#pc=`, JWT, hash, snapshot e segredos do `.env`.
  - O `scripts/logs-check.sh` confere isso a cada deploy.
- **HttpClient:** as linhas `Start/End processing HTTP request` estão desligadas (filtro `System.Net.Http.HttpClient=Warning` no código, em `Trace.cs`). O rastro vem das linhas `chk3`.
  - O ajuste antigo no compose, que não surtia efeito, foi removido.

## Containers

| Serviço (container) | Papel no Frame v7 | Passos | Porta interna | Porta publicada na VPS | Redes | Fala com |
|---|---|---|---|---|---|---|
| `canal` (`chk3-poc-frame-canal-1`) | Canal fake (a "loja"). Cria a sessão no servidor e embute o iframe | 1, 4 | 8080 | `127.0.0.1:18180` | fora | → `integration:8080` `POST /sessions` (JWT + `Idempotency-Key`) |
| `opcoes` (`chk3-poc-frame-opcoes-1`) | Tela de opções (fora da CDE). UI estática + proxy fino | 5, 6, 7, 14 | 8080 | `127.0.0.1:18181` | fora | → `integration:8090` `/internal/v1/payment-credential/validate` (6) e `/internal/v1/typing-credential` (7) |
| `cartao` (`chk3-poc-frame-cartao-1`) | Web App Cartão (CDE, endpoint pay). Página de digitação no iframe filho | 8, 9, 14 | 8080 | `127.0.0.1:18182` | cde | → `integration:8090` `/internal/v1/typing-credential/validate` (8). → `autorizacao:8080` `/authorize` (9) |
| `autorizacao` (`chk3-poc-frame-autorizacao-1`) | API Autorização (CDE, sem banco) | 9 a 13 | 8080 | não publicada | cde, vpn | → `integration:8090` `/internal/v1/attempts` (10) e `/internal/v1/attempts/{localRef}/result` (13). → `gateway-mock:8080` (11, 12) |
| `integration` (`chk3-poc-frame-integration-1`) | Integration API, dona do estado | 2, 3, 4, 6, 7, 8, 10, 13 | 8080 (pública), 8090 (interna) | `127.0.0.1:18183` → 8080 | fora, cde, dados | → `postgres:5432` |
| `gateway-mock` (`chk3-poc-frame-gateway-mock-1`) | Gateway MCN simulado | 11, 12 | 8080 | não publicada | vpn (internal) | Só recebe do `autorizacao` |
| `postgres` (`chk3-poc-frame-postgres-1`) | Postgres 17. Volume `chk3-poc-frame_pgdata` | 3, 6, 7, 10, 13 | 5432 | não publicada | dados (internal) | Só a `integration` acessa |

> **Os logs vivem no container.** Um `docker compose up --build` (todo deploy) ou um `down` recria os containers e **apaga os logs anteriores**. O estado da transação continua no Postgres.

## Ferramentas comuns

```bash
# Todos os serviços ao vivo, só as linhas de passo, legíveis
docker compose logs -f --no-color --no-log-prefix --tail=0 \
  | jq --unbuffered -R -r 'fromjson? | select(.Category=="chk3") | .Timestamp + " " + .Message'

# Um serviço ao vivo (JSON cru)
docker compose logs -f -t --tail=50 autorizacao

# Uma transação inteira, todos os containers (cru)
TX=01a21473-ba9f-776d-95c2-a381ed0d3519
docker compose logs --no-color | grep "$TX"

# A mesma transação, em tabela ordenada por horário (inclui avisos, como o timeout do Gateway)
docker compose logs --no-color --no-log-prefix | grep "$TX" \
  | jq -r '[.Timestamp, (.State.servico // ([.Scopes[]?|select(.servico)|.servico][0])), (.State.passo // .LogLevel),
            (.State.resultado // .Message), (.State.status|tostring), (.State.duracaoMs|tostring)] | @tsv' | sort

# Só com jq, sem grep (filtra pelo campo, não pelo texto)
docker compose logs --no-color --no-log-prefix \
  | jq -R -r --arg tx "$TX" 'fromjson? | select(.Category=="chk3" and .State.transactionId==$tx) | .Timestamp + " " + .Message' | sort

# SQL no Postgres usando o usuário e o banco do ambiente do container (não exibe senha)
chk3sql() { docker compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 "$@"' sh "$@"; }
```

> Dentro de script lido por stdin (`ssh vps 'bash -s' < script.sh`), o `docker compose exec -T` consome o stdin. Use `chk3sql ... </dev/null` nesse caso.

Nos exemplos abaixo, cada linha mostra o `Timestamp` e o `Message` da linha JSON, sem os campos `(null)`.

---

## `canal`

**O que registra:**
- `passo=1-4`, uma linha por `POST /checkout`, com `transactionId`, `orderId` e `resultado`:
  - `sessao_<estado>_<frame_embutido|sem_frame>_http<status do POST /sessions>`;
  - ou `erro_integration_<status>` (nesse caso, sem orderId).
- O escopo tem o correlation id gerado antes do `POST /sessions`.
- A `frameUrl` (com `#pc=`) e o JWT nunca vão para o log.

**Ao vivo:** `docker compose logs -f --no-color --no-log-prefix --tail=20 canal | jq --unbuffered -R -r 'fromjson? | .Timestamp + " " + .Message'`

**Filtrar por transação:** `docker compose logs --no-color canal | grep "$TX"`

**Exemplo real:**

```
2026-11-25T20:44:18.471Z chk3 passo=1-4 servico=canal transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 orderId=PED-1001 resultado=sessao_Criada_frame_embutido_http201 status=200 duracaoMs=11 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
```

O id gerado no canal antes do `POST /sessions` fica no escopo:

```bash
docker compose logs --no-color --no-log-prefix canal | grep "$TX" | jq -r '[.Scopes[]?|select(.servico)|.correlationId][0] + " -> " + .State.transactionId'
# 16fdfe88-3eb6-4a45-b207-e70bf1069233 -> 01a21473-ba9f-776d-95c2-a381ed0d3519
```

## `opcoes`

**O que registra:**
- `passo=6`: uma linha por validação da credencial, inclusive reapresentações. Resultados `valida` ou `6a_recusada_integration_401`.
- `passo=7`: credencial de digitação emitida.
- O `transactionId` vem da resposta da Integration. A credencial vai em header e não é registrada.

**Ao vivo:** `docker compose logs -f --no-color --no-log-prefix --tail=20 opcoes | jq --unbuffered -R -r 'fromjson? | .Timestamp + " " + .Message'`

**Filtrar por transação:** `docker compose logs --no-color opcoes | grep "$TX"`

**Recusas (6a), que não têm transactionId:**

```bash
docker compose logs --no-color --no-log-prefix opcoes | jq -R -r 'fromjson? | select(.State.resultado? // "" | startswith("6a")) | .Timestamp + " " + .Message'
```

**Exemplo real:**

```
2026-11-25T20:44:19.975Z chk3 passo=6 servico=opcoes transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 orderId=PED-1001 resultado=valida status=200 duracaoMs=11 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:21.732Z chk3 passo=6 servico=opcoes transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 orderId=PED-1001 resultado=valida status=200 duracaoMs=4 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:22.377Z chk3 passo=7 servico=opcoes transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 resultado=credencial_digitacao_emitida status=200 duracaoMs=8 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
```

A segunda linha do passo 6 é o "Recarregar frame" do E2E (reapresentação sem consumir).

## `cartao`

**O que registra:**
- `passo=8`: página de digitação entregue, ou `recusada_integration_401`.
- `passo=9-14`: resultado devolvido ao navegador, que vira o `postMessage` do passo 14. Valores possíveis:
  - `Autorizada`, `Recusada`, `Indeterminada`;
  - `TentativaJaRegistrada` (10b);
  - `ValidacaoFalhou_<código>` (9a);
  - `SessaoInvalida` (10a).
- O PAN nunca é registrado.

**Ao vivo:** `docker compose logs -f --no-color --no-log-prefix --tail=20 cartao | jq --unbuffered -R -r 'fromjson? | .Timestamp + " " + .Message'`

**Filtrar por transação:** `docker compose logs --no-color cartao | grep "$TX"`

**Pagamentos lentos** (timeouts do Gateway passam de 3000 ms):

```bash
docker compose logs --no-color --no-log-prefix cartao | jq -R -r 'fromjson? | select(.State.passo?=="9-14" and .State.duracaoMs > 2000) | .Timestamp + " " + .Message'
```

**Exemplo real:**

```
2026-11-25T20:44:22.759Z chk3 passo=8 servico=cartao transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 resultado=pagina_digitacao_entregue status=200 duracaoMs=6 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:24.462Z chk3 passo=9-14 servico=cartao transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 resultado=Autorizada status=200 duracaoMs=23 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
```

## `autorizacao`

**O que registra** (uma linha por passo, na mesma requisição):
- `passo=9`: `validacao_ok` ou `9a_<código>` (`snapshot_invalido`, `cartao_invalido`, `bandeira_nao_aceita`, `parcelas_invalidas`, `pan_nao_permitido_no_poc`). Só o código; nunca dado de cartão.
- `passo=10`: `10c_registrada` (com `localRef`), `10b_ja_registrada_<estado>` ou `10a_sessao_invalida`.
- `passo=11-12`: `<resultado>_codigo_<código do Gateway>` ou `Indeterminada_timeout_12b`, com a duração da chamada ao Gateway.
- `passo=13`: `<resultado>_registrado` ou `falha_registro`.
- Mais o aviso `Gateway sem resposta no prazo para localRef … transactionId …` (Warning) e o erro `Falha ao registrar resultado …`.

**Ao vivo:** `docker compose logs -f --no-color --no-log-prefix --tail=20 autorizacao | jq --unbuffered -R -r 'fromjson? | .Timestamp + " " + .LogLevel + " " + .Message'`

**Filtrar por transação:** `docker compose logs --no-color autorizacao | grep "$TX"`. O `grep "$LOCALREF"` também funciona.

**Exemplo real.** Aprovada:

```
2026-11-25T20:44:24.441Z chk3 passo=9 servico=autorizacao transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 resultado=validacao_ok status=(null) duracaoMs=0 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:24.451Z chk3 passo=10 servico=autorizacao transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 orderId=PED-1001 resultado=10c_registrada status=201 duracaoMs=9 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:24.455Z chk3 passo=11-12 servico=autorizacao transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 resultado=Autorizada_codigo_00 status=200 duracaoMs=3 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:24.461Z chk3 passo=13 servico=autorizacao transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 resultado=Autorizada_registrado status=(null) duracaoMs=6 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
```

Indeterminada (timeout do Gateway):

```
2026-11-25T20:44:42.874Z Gateway sem resposta no prazo para localRef be290d39-a437-4f89-a037-fb30397e39b9 transactionId 01a21473-f707-7496-a32b-27401c121377
2026-11-25T20:44:42.874Z chk3 passo=11-12 servico=autorizacao transactionId=01a21473-f707-7496-a32b-27401c121377 localRef=be290d39-a437-4f89-a037-fb30397e39b9 resultado=Indeterminada_timeout_12b status=(null) duracaoMs=3002 correlationId=01a21473-f707-7496-a32b-27401c121377
```

## `integration`

**O que registra** (uma linha por requisição):
- `passo=2-4`: `3c_criada`, `3b_nova_credencial`, `3b_estado_<estado>`, `3a_conteudo_diferente`, `2_canal_nao_autorizado`, `2_pedido_invalido`, `2a_campos_adicionais_invalidos`, `2b_parcelas_invalidas`. O `orderId` só aparece depois de validado; pedido recusado não vai para o log.
- `passo=6`: `valida` ou `6a_recusada`.
- `passo=7`: `emitida`, `recusada` ou `limite_credenciais`.
- `passo=8`: `valida` ou `recusada`.
- `passo=10`: `10c_registrada` (com `localRef`), `10b_ja_registrada_<estado>` ou `10a_sessao_invalida`.
- `passo=13`: `<resultado>_outbox_pagamento.<resultado>` (estado e evento gravados juntos), `ja_registrado_<resultado>` ou `resultado_ja_registrado`.
- `passo=-`: requisição sem passo, com a `rota` e o status. Exemplos: chave interna errada (401) ou porta errada (404).
- Na subida, também registra migrações e os avisos esperados do POC: DataProtection efêmero, e `Overriding address(es)` (o app escuta em 8080 e 8090).

**Ao vivo:** `docker compose logs -f --no-color --no-log-prefix --tail=20 integration | jq --unbuffered -R -r 'fromjson? | .Timestamp + " " + .Message'`

**Filtrar por transação:** `docker compose logs --no-color integration | grep "$TX"`

**Exemplo real:**

```
2026-11-25T20:44:18.469Z chk3 passo=2-4 servico=integration transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 orderId=PED-1001 resultado=3c_criada status=201 duracaoMs=7 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:19.974Z chk3 passo=6 servico=integration transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 orderId=PED-1001 resultado=valida status=200 duracaoMs=3 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:22.376Z chk3 passo=7 servico=integration transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 resultado=emitida status=200 duracaoMs=6 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:22.757Z chk3 passo=8 servico=integration transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 resultado=valida status=200 duracaoMs=3 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:24.449Z chk3 passo=10 servico=integration transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 orderId=PED-1001 resultado=10c_registrada status=201 duracaoMs=7 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:24.461Z chk3 passo=13 servico=integration transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 resultado=Autorizada_outbox_pagamento.autorizada status=200 duracaoMs=5 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
```

## `gateway-mock`

**O que registra:**
- `passo=11-12`, uma linha por autorização: `approved_00`, `declined_05`, `timeout_simulado_cliente_desistiu` (status 499, quando a API Autorização desiste no prazo) ou `pan_nao_e_de_teste`.
- O `localRef` vem do campo `reference` do contrato do mock.
- O `transactionId` vem do header `X-Correlation-Id` enviado pela API Autorização: o contrato do Gateway não tem esse campo.

**Ao vivo:** `docker compose logs -f --no-color --no-log-prefix --tail=20 gateway-mock | jq --unbuffered -R -r 'fromjson? | .Timestamp + " " + .Message'`

**Filtrar por transação:** `docker compose logs --no-color gateway-mock | grep "$TX"`

**Exemplo real.** Aprovada e timeout:

```
2026-11-25T20:44:24.454Z chk3 passo=11-12 servico=gateway-mock transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 resultado=approved_00 status=200 duracaoMs=0 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519
2026-11-25T20:44:42.872Z chk3 passo=11-12 servico=gateway-mock transactionId=01a21473-f707-7496-a32b-27401c121377 localRef=be290d39-a437-4f89-a037-fb30397e39b9 resultado=timeout_simulado_cliente_desistiu status=499 duracaoMs=3000 correlationId=01a21473-f707-7496-a32b-27401c121377
```

Linha JSON completa (as demais seguem o mesmo formato):

```json
{"Timestamp":"2026-11-25T20:44:24.454Z","EventId":0,"LogLevel":"Information","Category":"chk3","Message":"chk3 passo=11-12 servico=gateway-mock transactionId=01a21473-ba9f-776d-95c2-a381ed0d3519 localRef=e68af5d8-384c-470f-9c10-ce323e011ad5 orderId=(null) resultado=approved_00 status=200 duracaoMs=0 correlationId=01a21473-ba9f-776d-95c2-a381ed0d3519 rota=(null)","State":{"passo":"11-12","servico":"gateway-mock","transactionId":"01a21473-ba9f-776d-95c2-a381ed0d3519","localRef":"e68af5d8-384c-470f-9c10-ce323e011ad5","orderId":null,"resultado":"approved_00","status":200,"duracaoMs":0,"correlationId":"01a21473-ba9f-776d-95c2-a381ed0d3519","rota":null,"{OriginalFormat}":"..."},"Scopes":[{"SpanId":"5fa3d90fef80a2e2","TraceId":"2cbbcc7273cb3f16842ea3f8e586fd61","ParentId":"933140a6fda6d69f"},{"ConnectionId":"0HNQB6J88RA4U"},{"RequestId":"0HNQB6J88RA4U:00000001","RequestPath":"/poc/v1/authorize"},{"correlationId":"01a21473-ba9f-776d-95c2-a381ed0d3519","servico":"gateway-mock"}]}
```

O `{OriginalFormat}` e os `Message` dos escopos foram encurtados aqui.
- `TraceId`/`ParentId` são do W3C `traceparent`, que o .NET já propaga dentro de uma mesma requisição.
- O `correlationId` do último escopo é o `X-Correlation-Id` recebido.

## `postgres`

**O que registra:** só eventos do servidor (partida, checkpoints). Consultas não são registradas (`log_statement=none`).
O estado da transação está nas tabelas.

**Ao vivo:** `docker compose logs -f -t --tail=20 postgres`

**Filtrar por transação:** use o SQL abaixo.

**Exemplo real:**

```
2026-11-25 20:27:02.451 UTC [28] LOG:  checkpoint starting: time
```

### Tabelas (de `src/Integration/Migrations/0001_inicial.sql`)

- `payment_sessions`: uma linha por pagamento/sessão.
  - Colunas: `transaction_id`, `channel_id`, `idempotency_key`, `order_id`, `amount_cents`, `currency`, `installments`, `state`, `created_at`, `expires_at`, `updated_at`.
  - `state`: `Criada` → `AutorizacaoSolicitada` → `Autorizada` / `Recusada` / `Indeterminada`.
- `credentials`: só o hash das credenciais.
  - Colunas: `kind` (`pagamento`/`digitacao`), `transaction_id`, `created_at`, `expires_at`.
  - Não selecione `hash`.
- `attempts`: tentativa única por sessão (PK `transaction_id`).
  - Colunas: `local_ref`, `brand`, `installments`, `stage` (`envio_iniciado` → `resultado_registrado`), `result`, `gateway_code`, `created_at`, `updated_at`.
- `outbox`: evento gravado na mesma transação SQL do resultado.
  - Colunas: `id`, `transaction_id`, `event_type`, `payload`, `created_at`, `delivered_at`.
  - O worker de entrega (15/16) está fora do POC, então `delivered_at` fica sempre nulo.
- `channels`: cadastro dos canais.

### Consultas SQL

```bash
# Últimas sessões
chk3sql -c "SELECT transaction_id, channel_id, order_id, amount_cents, currency, state, created_at, updated_at
            FROM payment_sessions ORDER BY created_at DESC LIMIT 10;"

# Uma transação completa: sessão + tentativa, credenciais (sem hash) e outbox
chk3sql -v tx="$TX" <<'SQL'
SELECT p.transaction_id, p.channel_id, p.order_id, p.idempotency_key, p.amount_cents, p.currency, p.state,
       p.created_at, p.expires_at, p.updated_at,
       a.local_ref, a.brand, a.installments, a.stage, a.result, a.gateway_code,
       a.created_at AS attempt_at, a.updated_at AS result_at
FROM payment_sessions p LEFT JOIN attempts a USING (transaction_id)
WHERE p.transaction_id = :'tx' \gx
SELECT kind, created_at, expires_at FROM credentials WHERE transaction_id = :'tx' ORDER BY created_at;
SELECT id, event_type, payload, created_at, delivered_at FROM outbox WHERE transaction_id = :'tx';
SQL

# Tentativas recentes
chk3sql -c "SELECT transaction_id, local_ref, brand, installments, stage, result, gateway_code, created_at, updated_at
            FROM attempts ORDER BY created_at DESC LIMIT 10;"

# Outbox recente e quantos eventos não foram entregues
chk3sql -c "SELECT id, transaction_id, event_type, payload, created_at, delivered_at FROM outbox ORDER BY id DESC LIMIT 10;"
chk3sql -c "SELECT count(*) AS outbox_nao_entregue FROM outbox WHERE delivered_at IS NULL;"

# Contagem por estado, e sessões que pedem atenção
chk3sql -c "SELECT state, count(*) FROM payment_sessions GROUP BY state ORDER BY state;"
chk3sql -c "SELECT p.transaction_id, p.state, a.stage, a.result, p.updated_at
            FROM payment_sessions p LEFT JOIN attempts a USING (transaction_id)
            WHERE p.state IN ('AutorizacaoSolicitada','Indeterminada') ORDER BY p.updated_at DESC LIMIT 10;"

# Do localRef para a transação; e por pedido do canal
chk3sql -tAc "SELECT transaction_id, result FROM attempts WHERE local_ref = 'e68af5d8-384c-470f-9c10-ce323e011ad5'"
chk3sql -c "SELECT transaction_id, state, created_at FROM payment_sessions WHERE order_id = 'PED-1001' ORDER BY created_at DESC LIMIT 5;"
```

---

## Passo a passo: rastrear uma transação de ponta a ponta

1. **Ache o `transactionId`**, de um destes jeitos:
   - na tela do canal (cartão lateral "transactionId");
   - no SQL ("Últimas sessões" ou "por pedido");
   - nos logs do canal: `docker compose logs --no-color --no-log-prefix canal | jq -R -r 'fromjson? | select(.Category=="chk3") | .Timestamp + " " + .State.transactionId + " " + (.State.orderId//"")'`.
2. **Puxe todos os containers de uma vez:**

   ```bash
   TX=01a21473-ba9f-776d-95c2-a381ed0d3519
   docker compose logs --no-color | grep "$TX"                                     # cru
   docker compose logs --no-color --no-log-prefix | grep "$TX" \
     | jq -r '[.Timestamp, (.State.servico // ([.Scopes[]?|select(.servico)|.servico][0])), (.State.passo // .LogLevel),
              (.State.resultado // .Message), (.State.status|tostring), (.State.duracaoMs|tostring)] | @tsv' | sort
   ```

3. **Confira o estado final no banco**: a consulta "uma transação completa" mostra estado, `local_ref`, código do Gateway e evento no outbox.
4. **Se o estado for `Indeterminada`:**
   - deve haver `autorizacao passo=11-12 … Indeterminada_timeout_12b` e o Warning "Gateway sem resposta no prazo";
   - no `gateway-mock` aparece `timeout_simulado_cliente_desistiu`.
5. **Se o estado ficar em `AutorizacaoSolicitada`:**
   - procure `autorizacao passo=13 … falha_registro` e o erro `Falha ao registrar resultado …`;
   - a reconciliação (worker, passo 15) está fora do POC.

Saída real do passo 2 (tabela) na VPS, transação **aprovada feita no navegador**. São 18 linhas e todos os containers de aplicação aparecem:

```
2026-11-25T20:44:18.469Z  integration   2-4    3c_criada                               201  7
2026-11-25T20:44:18.471Z  canal         1-4    sessao_Criada_frame_embutido_http201    200  11
2026-11-25T20:44:19.974Z  integration   6      valida                                  200  3
2026-11-25T20:44:19.975Z  opcoes        6      valida                                  200  11
2026-11-25T20:44:21.731Z  integration   6      valida                                  200  3
2026-11-25T20:44:21.732Z  opcoes        6      valida                                  200  4
2026-11-25T20:44:22.376Z  integration   7      emitida                                 200  6
2026-11-25T20:44:22.377Z  opcoes        7      credencial_digitacao_emitida            200  8
2026-11-25T20:44:22.757Z  integration   8      valida                                  200  3
2026-11-25T20:44:22.759Z  cartao        8      pagina_digitacao_entregue               200  6
2026-11-25T20:44:24.441Z  autorizacao   9      validacao_ok                            null 0
2026-11-25T20:44:24.449Z  integration   10     10c_registrada                          201  7
2026-11-25T20:44:24.451Z  autorizacao   10     10c_registrada                          201  9
2026-11-25T20:44:24.454Z  gateway-mock  11-12  approved_00                             200  0
2026-11-25T20:44:24.455Z  autorizacao   11-12  Autorizada_codigo_00                    200  3
2026-11-25T20:44:24.461Z  autorizacao   13     Autorizada_registrado                   null 6
2026-11-25T20:44:24.461Z  integration   13     Autorizada_outbox_pagamento.autorizada  200  5
2026-11-25T20:44:24.462Z  cartao        9-14   Autorizada                              200  23
```

- A recusada (`01a21473-d968-…`) teve as mesmas 18 linhas, com `declined_05` / `Recusada_codigo_05` / `Recusada`.
- A indeterminada (`01a21473-f707-…`) teve 19: as 18, mais o Warning do timeout. Ela traz `timeout_simulado_cliente_desistiu` (499, 3000 ms) / `Indeterminada_timeout_12b` (3002 ms) / `Indeterminada` (3020 ms).

## Teste automatizado: `scripts/logs-check.sh`

O teste roda no `scripts/deploy.sh`, depois do smoke. Também dá para rodar à mão: `./scripts/logs-check.sh`.

1. Faz três transações completas **a partir do canal**, como o navegador faria: aprovada, recusada e indeterminada.
2. Falha se aparecer nos logs:
   - qualquer credencial `pc_`/`tc_`, snapshot ou fragment daquela execução (comparação exata);
   - qualquer segredo do `.env`;
   - PAN de teste, campos de cartão (`pan`, `cvv`, `expMonth`, `expYear`, `number`, `name`), `#pc=`, JWT ou base64url de JSON (`eyJ…`), `Bearer`, hash hex de 64 caracteres, ou o nome digitado no E2E.
3. Falha se o `transactionId` de alguma das três não aparecer em **cada** serviço com os passos esperados:
   - canal `1-4`;
   - opcoes `6 7`;
   - cartao `8 9-14`;
   - integration `2-4 6 7 8 10 13`;
   - autorizacao `9 10 11-12 13`;
   - gateway-mock `11-12`.
4. Falha se o `X-Correlation-Id` recebido não for o transactionId do passo 7 em diante (exceção: cartao passo 8; ver acima).

`LOGS_SINCE=<UTC ISO> ./scripts/logs-check.sh` amplia a busca de dados sensíveis, por exemplo para incluir um E2E no navegador rodado antes.

## O que ainda não aparece no log, e por quê

- **Passo 5 (a Tela abre e lê o fragment):**
  - acontece no navegador, e o fragment `#pc=` nunca chega ao servidor, por desenho;
  - o `GET /` da Tela é estático e não é registrado;
  - o primeiro registro da Tela é o passo 6.
- **Passo 14 (`postMessage` para a Tela e para o canal):**
  - acontece só no navegador;
  - o que fica no servidor é a resposta que o originou (`cartao passo=9-14`).
  - Erros de tela no navegador (ex.: falha de rede mostrada ao usuário) não são enviados ao servidor.
- **Passos 1, 2, 3 e 4 e passos 11 e 12 em linhas agrupadas:**
  - 1-4: o canal registra a ida e volta do `POST /sessions`, e a Integration registra o 2-4 com o resultado do 3 (3a/3b/3c);
  - 11-12: a chamada ao Gateway e a resposta formam uma operação só.
- **`X-Correlation-Id` antes do transactionId:**
  - no `POST /sessions` e no passo 6, o header ainda não é o transactionId (gerado no canal / na Tela);
  - no passo 8, a navegação do iframe não leva header;
  - nos três casos, as linhas trazem o `transactionId` no campo próprio, então o grep acha todas.
- **Retenção:**
  - os logs ficam no driver padrão do Docker (`json-file`), sem agregação;
  - somem quando o container é recriado (todo deploy).
  - Para guardar, seria preciso um coletor (ex.: Loki/LGTM) ou `logging.options` com rotação. Está fora do escopo do POC.
- **Escopos do ASP.NET:**
  - o .NET acrescenta `RequestPath` (path bruto) e `TraceId`;
  - nos fluxos do POC, o path não tem dado sensível (o máximo é o `localRef` no passo 13), e query string é recusada antes;
  - a linha `chk3` usa só o padrão da rota.

## Atualização 27/11/2026: worker, XML do Gateway, token do frame e Loki

Commits `13560fc`..`0ad9381`. Exemplos da VPS, 27/11/2026 (UTC = Lisboa).

**Novo container `worker`** (`chk3-poc-frame-worker-1`, redes `dados` e `saida`, sem porta publicada):

| passo | resultado | quando |
|---|---|---|
| `16` | `webhook_entregue_<host>_http<status>_tentativa<n>` / `webhook_falhou_<host>_http<status>_tentativa<n>` / `webhook_sem_url_https` | cada envio do outbox ao canal. A URL completa não vai para o log, só o host |
| `15` | `expurgo_token_frame_<n>_para_historico` | lote de tokens expirados/usados movidos para `frame_token_history` (sem transactionId na linha) |

O `transactionId` e o `orderId` do evento ficam na linha do passo 16, então o `grep "$TX"` agora acha também o webhook:

```
2026-11-27T00:38:17.168Z chk3 passo=16 servico=worker transactionId=01a21a70-4140-7e73-a610-04b8452aa661 localRef=(null) orderId=SMOKE-aprova resultado=webhook_entregue_webhook.site_http200_tentativa1 status=200 duracaoMs=717
2026-11-27T00:38:12.426Z chk3 passo=15 servico=worker transactionId=(null) ... resultado=expurgo_token_frame_125_para_historico
```

**gateway-mock, passo 11-12:** o mock agora recebe o `<transaction-request>` XML (ver `contrato-json-xml.md`). O `resultado` diz se o XML passou na validação, com parcelas e método:
- `xml_ok_n1_m1_approved_00`, `xml_ok_n3_m2_approved_00` (3x, método parcelado provisório 2), `xml_ok_n1_m1_declined_05`, `xml_ok_n1_m1_timeout_simulado_cliente_desistiu`;
- `xml_invalido_<campo>` (ex.: `xml_invalido_authorize.payment.numberOfPayments`), com status 400; merchant errado dá 401.
- O `orderId` da linha é o `<reference>` do XML. O XML bruto, PAN, CVV, validade, nome e a merchantKey nunca são registrados.

**autorizacao:** novo `9a_nome_invalido` (nome do portador). As parcelas acima do máximo da sessão continuam como `9a_parcelas_invalidas` (422).

**integration:** novos códigos do `POST /sessions`: `2_valor_invalido` (sem `amount`), `2a_billing_<campo>_invalido`, `2a_url_return_invalida`, `2a_soft_descriptor_invalido`. O valor recusado nunca vai para o log.

**Postgres** (migração `0002_token_frame_parcelas_webhook.sql`, aplicada no volume existente, nada apagado):
- `credentials` → **`frame_tokens`** (+ `brands`, `max_installments`, `used_at`). Continua sem selecionar `hash`.
- **`frame_token_history`**: metadados dos tokens expurgados (sem token/hash).
- `payment_sessions` + `billing` (jsonb), `url_return`, `soft_descriptor`; `channels` + `webhook_url`; `outbox` + `event_id`, `attempts`, `next_attempt_at`, `last_status`, `last_error`.

```bash
q() { docker compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$0"' "$1"; }
q "select kind, transaction_id, created_at, expires_at, used_at from frame_tokens order by created_at desc limit 10"
q "select motivo, count(*) from frame_token_history group by motivo"
q "select event_type, attempts, last_status, last_error, delivered_at, next_attempt_at from outbox order by created_at desc limit 10"
```

**Retenção (substitui o item "Retenção" acima):** os logs também vão para o **Loki** (7 dias) e sobrevivem ao deploy. O Docker passou a rotacionar (`json-file` 10 MB × 3). Para ver no navegador, ver [`grafana.md`](grafana.md). O `docker compose logs | grep` continua valendo para o que ainda está no container.

**`scripts/logs-check.sh`** agora também:
- inclui o `worker` e espera até 45 s pelo passo 16 de cada transação;
- faz um caso parcelado 3x (máximo 6) e confere `xml_ok_n<n>_m<método>_` no gateway-mock;
- envia billing de teste e procura nos logs esses valores, tags XML (`<cardNumber>` etc.), a URL completa do webhook e os segredos novos (`GATEWAY_MERCHANT_KEY`, `WEBHOOK_SIGNING_SECRET`, `GRAFANA_ADMIN_PASSWORD`).

### Gateway real e XML mascarado (27/11/2026, tarde)

- A API Autorização agora escreve duas linhas `chk3gw` por autorização (categoria `chk3.gateway`): `passo=11 direcao=ida` (XML enviado) e `passo=12 direcao=volta` (status HTTP e corpo da resposta), **sempre mascarados** (ver `contrato-json-xml.md` §6). Elas têm `transactionId` e `localRef`, então o `grep "$TX"` também as acha.
- O `resultado` do `autorizacao passo=11-12` ganhou o destino: `Autorizada_codigo_00_mock`, `Indeterminada_real_http404_vazio`, `Indeterminada_timeout_12b_real`.
- Ver no terminal: `docker compose logs --no-color --no-log-prefix autorizacao | jq -R -r 'fromjson? | select(.Category=="chk3.gateway") | .Message'`.
- O `logs-check.sh` confere que cada transação tem ida e volta e que nenhum campo sensível do XML sai sem máscara.


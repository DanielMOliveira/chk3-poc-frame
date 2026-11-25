# Operação: containers, logs e rastreio de uma transação (POC CHK3 frame v7)

Fontes: `docker-compose.yml` e o código em `src/`, no commit em que este arquivo entrou.
Todos os comandos foram executados na VPS, no diretório do app, em 25/11/2026. Os trechos de log e de SQL são reais, gerados pelo `scripts/smoke.sh` e pelo E2E no navegador.
Horários: o `docker compose logs -t` e o Postgres mostram UTC (`Z` / `+00`). Em novembro, Lisboa também está em UTC+0.

```bash
cd /home/daniel/projetos/chk3-poc-frame   # VPS. Em outra máquina, rode na raiz do clone.
```

## Resumo: dá para seguir uma transação pelos logs?

**Hoje, não de ponta a ponta.**

- O id da transação é o **`transactionId`**: UUIDv7, `payment_sessions.transaction_id`, devolvido no `POST /sessions`.
  - Ele **não aparece em nenhum log**: `docker compose logs | grep <transactionId>` retorna 0 linhas.
- O único id que aparece em log é o **`localRef`** (`attempts.local_ref`). Ele é criado no passo 10 e só aparece no log do `autorizacao`:
  - na URL do passo 13, `POST .../internal/v1/attempts/<localRef>/result`;
  - no aviso de timeout do Gateway (12b).
- `canal`, `opcoes` e `cartao` registram só as chamadas HTTP de saída (método, URL sem id, duração, status).
  - Para ligá-las a uma transação, a única forma é a **janela de tempo** da sessão, tirada do banco.
  - Com transações simultâneas, essa janela mistura transações.
- `integration` e `gateway-mock` não registram nada por requisição.
- **O banco é a fonte de verdade** do estado: `payment_sessions`, `attempts`, `credentials` e `outbox`.

O que **não** aparece nos logs (verificado com `grep` nos logs de todos os serviços): PAN de teste, CVV, credencial `pc_`/`tc_`, `#pc=`, header `X-Payment-Credential`.

## Containers

| Serviço (container) | Papel no Frame v7 | Passos | Porta interna | Porta publicada na VPS | Redes | Fala com |
|---|---|---|---|---|---|---|
| `canal` (`chk3-poc-frame-canal-1`) | Canal fake (a "loja"). Cria a sessão no servidor e embute o iframe | 1, 4 | 8080 | `127.0.0.1:18180` | fora | → `integration:8080` `POST /sessions` (JWT + `Idempotency-Key`). O navegador abre o `frameUrl` (Tela de opções) |
| `opcoes` (`chk3-poc-frame-opcoes-1`) | Tela de opções (fora da CDE). UI estática + proxy fino para a Integration | 5, 6, 7, 14 | 8080 | `127.0.0.1:18181` | fora | → `integration:8090` `/internal/v1/payment-credential/validate` (6) e `/internal/v1/typing-credential` (7). O navegador faz POST do `tc` para o `cartao` (8) |
| `cartao` (`chk3-poc-frame-cartao-1`) | Web App Cartão (CDE, endpoint pay). Página de digitação no iframe filho | 8, 9, 14 | 8080 | `127.0.0.1:18182` | cde | → `integration:8090` `/internal/v1/typing-credential/validate` (8). → `autorizacao:8080` `/authorize` (9). `postMessage` para a Tela (14) |
| `autorizacao` (`chk3-poc-frame-autorizacao-1`) | API Autorização (CDE, sem banco). Valida o cartão, garante a tentativa única e chama o Gateway | 9 a 13 | 8080 | não publicada | cde, vpn | → `integration:8090` `/internal/v1/attempts` (10) e `/internal/v1/attempts/{localRef}/result` (13). → `gateway-mock:8080` `/poc/v1/authorize` (11, 12) |
| `integration` (`chk3-poc-frame-integration-1`) | Integration API. Dona do estado. Pública: `POST /sessions`. Interna: credenciais, tentativa e resultado | 2, 3, 4, 6, 7, 8, 10, 13 | 8080 (pública), 8090 (interna) | `127.0.0.1:18183` → 8080. A 8090 não é publicada | fora, cde, dados | → `postgres:5432`. Recebe de `canal` (8080) e de `opcoes`, `cartao` e `autorizacao` (8090, `X-Internal-Key`) |
| `gateway-mock` (`chk3-poc-frame-gateway-mock-1`) | Gateway MCN simulado. Aprova, recusa ou demora conforme o PAN de teste | 11, 12 | 8080 | não publicada | vpn (internal) | Só recebe do `autorizacao` (`X-Gateway-Key`) |
| `postgres` (`chk3-poc-frame-postgres-1`) | Postgres 17. Volume `chk3-poc-frame_pgdata` | 3, 6, 7, 10, 13 | 5432 | não publicada | dados (internal) | Só a `integration` acessa |

Também existe o serviço `tests` (profile `test`, só sobe com `./scripts/test.sh`).

Na VPS, o acesso é pelo túnel SSH: `localhost:8080`, `:8081`, `:8082` e `:8083` no PC → `127.0.0.1:18180` a `18183` na VPS.

Níveis de log configurados no compose:
- `Default=Information`, `Microsoft.AspNetCore=Warning`: o ASP.NET não registra as requisições recebidas.
- `System.Net.Http.HttpClient=Warning`: **na prática não está surtindo efeito**. As linhas `Start/End processing HTTP request` do HttpClient aparecem em nível `info`, e hoje elas são o único rastro por requisição. Ver "Lacunas".

## Ferramentas comuns

```bash
# Todos os serviços ao vivo, só linhas novas, com horário
docker compose logs -f -t --tail=0

# Um serviço ao vivo (últimas 50 linhas e segue)
docker compose logs -f -t --tail=50 autorizacao

# Uma janela de tempo (UTC), todos os serviços
docker compose logs -t --no-color --since 2026-11-25T20:18:28Z --until 2026-11-25T20:18:29Z

# SQL no Postgres usando o usuário e o banco que já estão no ambiente do container (não exibe senha)
chk3sql() { docker compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 "$@"' sh "$@"; }
chk3sql -c "SELECT now();"
```

> Dentro de script lido por stdin (`ssh vps 'bash -s' < script.sh`), o `docker compose exec -T` consome o stdin. Use `chk3sql ... </dev/null` nesse caso.

---

## `canal`

**O que registra hoje:**
- Partida do app (`Microsoft.Hosting.Lifetime`).
- Uma linha `info` do HttpClient por `POST http://integration:8080/sessions`, com duração e status.
- **Não registra** `transactionId`, pedido nem valor.

**Ao vivo:** `docker compose logs -f -t --tail=50 canal`

**Filtrar por transação:**
- Não há id no log. Use a janela de tempo da sessão (`payment_sessions.created_at`); ver "Passo a passo".
- O `transactionId` aparece na tela do canal (cartão lateral "transactionId").

**Exemplo real** (E2E no navegador, pelo túnel):

```
canal-1  | 2026-11-25T20:19:19.349495796Z info: System.Net.Http.HttpClient.integration.LogicalHandler[100]
canal-1  | 2026-11-25T20:19:19.349559625Z       Start processing HTTP request POST http://integration:8080/sessions
canal-1  | 2026-11-25T20:19:19.431835619Z info: System.Net.Http.HttpClient.integration.LogicalHandler[101]
canal-1  | 2026-11-25T20:19:19.431845267Z       End processing HTTP request after 85.4655ms - 201
```

## `opcoes`

**O que registra hoje:**
- Linhas `info` do HttpClient para `.../payment-credential/validate` (passo 6, inclusive as reapresentações) e `.../typing-credential` (passo 7), com status.
- A credencial vai em header e **não** é registrada.
- 401 = credencial inválida, expirada ou já usada (6a).

**Ao vivo:** `docker compose logs -f -t --tail=50 opcoes`

**Filtrar por transação:** sem id; use a janela de tempo. Para ver só as recusas:

```bash
docker compose logs -t --no-color opcoes | grep -E "End processing HTTP request .* - 401"
```

**Exemplo real** (caso aprova do smoke):

```
opcoes-1  | 2026-11-25T20:18:28.768859569Z       Start processing HTTP request POST http://integration:8090/internal/v1/payment-credential/validate
opcoes-1  | 2026-11-25T20:18:28.773621229Z       End processing HTTP request after 4.7711ms - 200
opcoes-1  | 2026-11-25T20:18:28.798284549Z       Start processing HTTP request POST http://integration:8090/internal/v1/typing-credential
opcoes-1  | 2026-11-25T20:18:28.804180458Z       End processing HTTP request after 5.929ms - 200
opcoes-1  | 2026-11-25T20:18:28.912428511Z       Start processing HTTP request POST http://integration:8090/internal/v1/payment-credential/validate
opcoes-1  | 2026-11-25T20:18:28.916157665Z       End processing HTTP request after 3.785ms - 401
```

A última linha é o teste 6a do smoke: a credencial deixa de valer depois da tentativa.

## `cartao`

**O que registra hoje:**
- Linhas `info` do HttpClient para `.../typing-credential/validate` (passo 8) e `http://autorizacao:8080/authorize` (passo 9), com duração e status.
- 200 = resultado (Autorizada, Recusada ou Indeterminada; o status HTTP não distingue). 409 = tentativa já registrada (10b). 422 = validação (9a/10a).
- O PAN **não** é registrado.

**Ao vivo:** `docker compose logs -f -t --tail=50 cartao`

**Filtrar por transação:** sem id; use a janela de tempo. A duração do `/authorize` ajuda a achar timeouts (cerca de 3 s com `GATEWAY_TIMEOUT_MS=3000`):

```bash
docker compose logs -t --no-color cartao | grep -E "End processing HTTP request after [0-9]{4,}\."
```

**Exemplo real** (caso aprova):

```
cartao-1  | 2026-11-25T20:18:28.814972820Z       Start processing HTTP request POST http://integration:8090/internal/v1/typing-credential/validate
cartao-1  | 2026-11-25T20:18:28.818779971Z       End processing HTTP request after 3.9278ms - 200
cartao-1  | 2026-11-25T20:18:28.836165840Z       Start processing HTTP request POST http://autorizacao:8080/authorize
cartao-1  | 2026-11-25T20:18:28.857312915Z       End processing HTTP request after 21.132ms - 200
cartao-1  | 2026-11-25T20:18:28.879944443Z       Start processing HTTP request POST http://autorizacao:8080/authorize
cartao-1  | 2026-11-25T20:18:28.886983565Z       End processing HTTP request after 7.0507ms - 409
```

O segundo `/authorize` é o teste 10b do smoke (segunda tentativa recusada).

## `autorizacao`

**O que registra hoje:**
- Linhas `info` do HttpClient para `.../attempts` (10; 201 = tentativa criada, 409 = já existia), `http://gateway-mock:8080/poc/v1/authorize` (11/12) e `.../attempts/<localRef>/result` (13).
- `warn: Gateway sem resposta no prazo para localRef <localRef>` (12b, vira Indeterminada).
- `fail: Falha ao registrar resultado da localRef <localRef>`, se o passo 13 falhar três vezes.
- É **o único serviço com id no log**: o `localRef`. O `transactionId` não aparece.

**Ao vivo:** `docker compose logs -f -t --tail=50 autorizacao`

**Filtrar por transação:**

```bash
LR=$(chk3sql -tAc "SELECT local_ref FROM attempts WHERE transaction_id = '01a2145c-1506-7ad1-8ab6-ee5bedcc2965'")
docker compose logs -t --no-color autorizacao | grep "$LR"
# Só avisos e falhas (timeouts do Gateway, falha no passo 13)
docker compose logs -t --no-color autorizacao | grep -A1 -E "^\S+\s+\| \S+ (warn|fail):"
```

**Exemplo real.** Caso aprova (`localRef bffdd195-...`):

```
autorizacao-1  | 2026-11-25T20:18:28.839938066Z       Start processing HTTP request POST http://integration:8090/internal/v1/attempts
autorizacao-1  | 2026-11-25T20:18:28.847597101Z       End processing HTTP request after 7.7711ms - 201
autorizacao-1  | 2026-11-25T20:18:28.847914156Z       Start processing HTTP request POST http://gateway-mock:8080/poc/v1/authorize
autorizacao-1  | 2026-11-25T20:18:28.851165424Z       End processing HTTP request after 3.2914ms - 200
autorizacao-1  | 2026-11-25T20:18:28.851281652Z       Start processing HTTP request POST http://integration:8090/internal/v1/attempts/bffdd195-fc72-4a2d-898e-e2cb3b3310fb/result
autorizacao-1  | 2026-11-25T20:18:28.856639922Z       End processing HTTP request after 5.3065ms - 200
```

Caso timeout (Indeterminada):

```
autorizacao-1  | 2026-11-25T20:18:32.418225859Z warn: Program[0]
autorizacao-1  | 2026-11-25T20:18:32.418279720Z       Gateway sem resposta no prazo para localRef 5aefe142-ce9c-4c72-9c5b-a27288b8d24b
autorizacao-1  | 2026-11-25T20:18:32.418417398Z       Start processing HTTP request POST http://integration:8090/internal/v1/attempts/5aefe142-ce9c-4c72-9c5b-a27288b8d24b/result
```

## `integration`

**O que registra hoje:**
- Partida: `Migração aplicada: <versão>` na primeira subida e `Postgres indisponível (tentativa N)` enquanto o banco não sobe.
- Dois avisos de inicialização, ambos esperados no POC:
  - `Overriding address(es) 'http://*:8080'`: o app escuta em 8080 e 8090;
  - `No XML encryptor configured`: DataProtection sem chave persistida.
- **Nada por requisição:** nem sessão criada, nem validação, nem tentativa ou resultado.
- Os dados da transação estão no Postgres. Ver as consultas SQL abaixo.

**Ao vivo:** `docker compose logs -f -t --tail=50 integration`

**Filtrar por transação:** não é possível pelo log; use o SQL.

**Exemplo real** (na subida, depois do deploy):

```
integration-1  | 2026-11-25T20:17:06.570101678Z       Now listening on: http://[::]:8080
integration-1  | 2026-11-25T20:17:06.570592547Z       Now listening on: http://[::]:8090
```

Durante o smoke e o E2E, `docker compose logs --since <início> integration` retornou **0 linhas**.

## `gateway-mock`

**O que registra hoje:** só a partida. Nenhuma linha por autorização.

**Ao vivo:** `docker compose logs -f -t --tail=50 gateway-mock`

**Filtrar por transação:**
- Não é possível.
- A chamada aparece no log do `autorizacao` (`POST http://gateway-mock:8080/poc/v1/authorize`).
- O mock recebe o `localRef` no campo `reference`, mas não o registra.
- O resultado fica em `attempts.result` e `attempts.gateway_code`: `00` aprovado, `05` recusado, vazio em timeout.

**Exemplo real:** depois do smoke e do E2E, `docker compose logs --since <início> gateway-mock` retornou **0 linhas**.

## `postgres`

**O que registra hoje:**
- Só eventos do servidor: partida, checkpoints e `database system is ready`.
- Consultas não são registradas (padrão `log_statement=none`).

**Ao vivo:** `docker compose logs -f -t --tail=50 postgres`

**Filtrar por transação:** use o SQL.

**Exemplo real:**

```
postgres-1  | 2026-11-25T20:17:02.176918175Z PostgreSQL Database directory appears to contain a database; Skipping initialization
postgres-1  | 2026-11-25T20:17:02.529519924Z 2026-11-25 20:17:02.529 UTC [1] LOG:  database system is ready to accept connections
```

### Tabelas (de `src/Integration/Migrations/0001_inicial.sql`)

- `payment_sessions`: uma linha por pagamento/sessão.
  - Colunas: `transaction_id`, `channel_id`, `idempotency_key`, `order_id`, `amount_cents`, `currency`, `installments`, `state`, `created_at`, `expires_at`, `updated_at`.
  - `state` vai de `Criada` para `AutorizacaoSolicitada` e depois `Autorizada`, `Recusada` ou `Indeterminada`.
- `credentials`: só o hash das credenciais.
  - Colunas: `kind` (`pagamento`/`digitacao`), `transaction_id`, `created_at`, `expires_at`.
  - Não selecione `hash`.
- `attempts`: tentativa única por sessão (PK `transaction_id`).
  - Colunas: `local_ref`, `brand`, `installments`, `stage` (`envio_iniciado` → `resultado_registrado`), `result`, `gateway_code`, `created_at`, `updated_at`.
- `outbox`: evento gravado na mesma transação SQL do resultado.
  - Colunas: `id`, `transaction_id`, `event_type` (`pagamento.autorizada|recusada|indeterminada`), `payload`, `created_at`, `delivered_at`.
  - O worker de entrega (15/16) está fora do POC, então `delivered_at` fica sempre nulo.
- `channels`: cadastro dos canais (`canal-fake`, `canal-ttl-curto`).

### Consultas SQL (testadas)

```bash
# Últimas sessões
chk3sql -c "SELECT transaction_id, channel_id, order_id, amount_cents, currency, state, created_at, updated_at
            FROM payment_sessions ORDER BY created_at DESC LIMIT 10;"

# Uma transação completa: sessão + tentativa, credenciais (sem hash) e outbox
TX=01a2145c-1506-7ad1-8ab6-ee5bedcc2965
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

# Contagem por estado, e sessões que pedem atenção (em autorização ou indeterminadas)
chk3sql -c "SELECT state, count(*) FROM payment_sessions GROUP BY state ORDER BY state;"
chk3sql -c "SELECT p.transaction_id, p.state, a.stage, a.result, p.updated_at
            FROM payment_sessions p LEFT JOIN attempts a USING (transaction_id)
            WHERE p.state IN ('AutorizacaoSolicitada','Indeterminada') ORDER BY p.updated_at DESC LIMIT 10;"

# Do localRef (log do autorizacao) para a transação
chk3sql -tAc "SELECT transaction_id, result FROM attempts WHERE local_ref = 'bffdd195-fc72-4a2d-898e-e2cb3b3310fb'"

# Por pedido do canal
chk3sql -c "SELECT transaction_id, state, created_at FROM payment_sessions WHERE order_id = 'SMOKE-aprova' ORDER BY created_at DESC LIMIT 5;"
```

Saída real da consulta "uma transação completa" (caso aprova do smoke):

```
transaction_id  | 01a2145c-1506-7ad1-8ab6-ee5bedcc2965
channel_id      | canal-fake
order_id        | SMOKE-aprova
idempotency_key | smoke-2f6697835d7320f0
amount_cents    | 1990
currency        | BRL
state           | Autorizada
created_at      | 2026-11-25 20:18:28.742827+00
expires_at      | 2026-11-25 20:48:28.742827+00
updated_at      | 2026-11-25 20:18:28.852301+00
local_ref       | bffdd195-fc72-4a2d-898e-e2cb3b3310fb
brand           | visa
installments    | 1
stage           | resultado_registrado
result          | Autorizada
gateway_code    | 00
attempt_at      | 2026-11-25 20:18:28.842165+00
result_at       | 2026-11-25 20:18:28.852301+00

   kind    |          created_at           |          expires_at
-----------+-------------------------------+-------------------------------
 pagamento | 2026-11-25 20:18:28.742827+00 | 2026-11-25 20:48:28.742827+00
 digitacao | 2026-11-25 20:18:28.801324+00 | 2026-11-25 20:48:28.742827+00

 id |      event_type      |                                     payload                                      |          created_at           | delivered_at
----+----------------------+----------------------------------------------------------------------------------+-------------------------------+--------------
 19 | pagamento.autorizada | {"state": "Autorizada", "transactionId": "01a2145c-1506-7ad1-8ab6-ee5bedcc2965"} | 2026-11-25 20:18:28.852301+00 |
```

---

## Passo a passo: rastrear uma transação de ponta a ponta

1. **Ache o `transactionId`.**
   - No navegador: o cartão lateral do canal mostra o `transactionId`.
   - Pelo banco: as consultas "Últimas sessões" ou "Por pedido do canal".
2. **Veja o estado no banco**: a consulta "uma transação completa".
   - `state`, `local_ref`, `result`, `gateway_code`, os horários de cada passo (`created_at` = 3c, credencial `digitacao` = 7, `attempt_at` = 10, `result_at` = 13) e o evento no `outbox`.
3. **Pegue o trecho de log de todos os serviços na janela da transação.** Ela vai da criação da sessão até 1 s depois do resultado:

   ```bash
   TX=01a2145c-1506-7ad1-8ab6-ee5bedcc2965
   read SINCE UNTIL < <(chk3sql -tA -F ' ' -c "SELECT to_char(p.created_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"'),
       to_char(COALESCE(a.updated_at, p.updated_at) AT TIME ZONE 'UTC' + interval '1 second','YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')
       FROM payment_sessions p LEFT JOIN attempts a USING (transaction_id) WHERE p.transaction_id = '$TX'")
   echo "$SINCE .. $UNTIL"
   docker compose logs -t --no-color --since "$SINCE" --until "$UNTIL" \
     | grep -E "processing HTTP request|warn:|fail:|Gateway sem resposta|Falha ao registrar" | grep -v "Start processing" | sort -k3
   ```

   - O `--since`/`--until` tem resolução de segundo.
   - Em uso real, com o navegador, a janela vai de minutos até o usuário digitar o cartão, e pode conter outras transações.
4. **Confirme a parte do Gateway pelo `localRef`:**

   ```bash
   LR=$(chk3sql -tAc "SELECT local_ref FROM attempts WHERE transaction_id = '$TX'")
   docker compose logs -t --no-color autorizacao | grep "$LR"
   ```

5. **Se o estado for `Indeterminada`:**
   - deve existir um `warn` "Gateway sem resposta no prazo para localRef …" com o mesmo `localRef`;
   - o `cartao` mostra o `/authorize` com cerca de 3000 ms.
6. **Se o estado ficou em `AutorizacaoSolicitada`:**
   - procure `fail: Falha ao registrar resultado da localRef …` no `autorizacao`;
   - a reconciliação (worker, passo 15) está fora do POC.

Saída real do passo 3 para a transação acima (`01a2145c-1506-…`, janela `20:18:28Z .. 20:18:29Z`), ordenada por horário e resumida às linhas `End`. O comando também devolve mais três linhas (409, 409 e 401), que são os testes 10b e 6a do smoke e foram omitidas aqui:

```
opcoes-1       | 2026-11-25T20:18:28.773621229Z       End processing HTTP request after 4.7711ms - 200     (6)
opcoes-1       | 2026-11-25T20:18:28.786572451Z       End processing HTTP request after 2.5803ms - 200     (6, reapresentação)
opcoes-1       | 2026-11-25T20:18:28.804180458Z       End processing HTTP request after 5.929ms - 200      (7)
cartao-1       | 2026-11-25T20:18:28.818779971Z       End processing HTTP request after 3.9278ms - 200     (8)
autorizacao-1  | 2026-11-25T20:18:28.847597101Z       End processing HTTP request after 7.7711ms - 201     (10)
autorizacao-1  | 2026-11-25T20:18:28.851165424Z       End processing HTTP request after 3.2914ms - 200     (11-12)
autorizacao-1  | 2026-11-25T20:18:28.856639922Z       End processing HTTP request after 5.3065ms - 200     (13)
cartao-1       | 2026-11-25T20:18:28.857312915Z       End processing HTTP request after 21.132ms - 200     (9 → 14)
```

A coluna de passos foi anotada à mão. As linhas `End` não trazem a URL, e só se sabe qual é a chamada pela linha `Start` anterior.

## Lacunas e proposta de correção (não implementada)

**Lacunas:**

| Serviço | Tem `transactionId` no log? | Tem `localRef`? | O que falta |
|---|---|---|---|
| canal | não | não | id da sessão criada |
| opcoes | não | não | passo (6/7), id e resultado |
| cartao | não | não | passo (8/9), id e resultado (Autorizada/Recusada/Indeterminada não aparece) |
| autorizacao | não | sim (13 e 12b) | `transactionId`, resultado e motivo do 9a |
| integration | não | não | nada por requisição: 3a/3b/3c, 6a, 10a/10b/10c, 13 |
| gateway-mock | não | não | nada por requisição |
| postgres | n/a | n/a | não precisa; o estado está nas tabelas |

Outras lacunas:
- Sem formato estruturado (JSON) nem `TraceId` nas linhas. Cada passo é uma requisição separada do navegador, então nem o `traceparent` que o HttpClient já propaga ligaria a transação inteira.
- O ajuste `Logging__LogLevel__System.Net.Http.HttpClient=Warning` do compose não está surtindo efeito (as linhas `info` do HttpClient aparecem). Se for "corrigido" sem a proposta abaixo, some o único rastro por requisição.

**Menor mudança proposta** (estimativa: cerca de 40 linhas, sem mudar contratos):

1. **Log estruturado:** em `src/Shared`, um `builder.Logging.AddJsonConsole(o => o.IncludeScopes = true)` comum aos seis apps. Cada linha passa a ter `Timestamp`, `Category`, `TraceId` e os campos abaixo.
2. **Correlation id = `transactionId`**, que já existe e já volta nas respostas da Integration (passos 3/4, 6, 8 e 10). Uma linha `Information` por passo, com campos fixos:
   `chk3 passo={Passo} transactionId={TransactionId} localRef={LocalRef} resultado={Resultado} ms={Ms}`
   - **integration:** 3a/3b/3c, 6/6a, 7, 8, 10a/10b/10c e 13 (com estado e `event_type` do outbox).
   - **opcoes:** 6 e 7, com o `transactionId` da resposta da Integration (em 401, `transactionId` vazio e `resultado=recusada`).
   - **cartao:** 8 e 9/14, usando o `transactionId` da validação do passo 8 e o resultado devolvido pelo `autorizacao`.
   - **autorizacao:** 9 (só o código do 9a, nunca dado de cartão), 10, 12 e 13. Leva `transactionId` (`snap.Sid`) e `localRef`, e o aviso de timeout passa a incluir o `transactionId`.
   - **gateway-mock:** `reference` (= `localRef`), resultado e código.
   - **canal:** `transactionId` e status do `POST /sessions`.
3. **Header `X-Correlation-Id: <transactionId>`** nas chamadas entre serviços a partir do passo 8, mais um `BeginScope` no servidor que o recebe. É opcional, porque o item 2 já resolve.
4. **Regras de segurança:** nunca registrar PAN, CVV, validade, nome, `pc_`, `tc_`, snapshot, JWT nem o header `X-Internal-Key`. Um teste xUnit lê os logs do smoke e falha se achar algum desses padrões.

Com isso, o rastreio vira:

```bash
docker compose logs -t --no-color | grep <transactionId>
```

Ele mostra todos os passos, de todos os serviços, sem depender de janela de tempo.

# Grafana: acompanhar uma transação do início ao fim

Versão de 27/11/2026. Fontes: `docker-compose.yml` e `observabilidade/`.

## Como funciona

```
containers chk3 (stdout JSON) ──> docker json-file ──> docker-proxy (API Docker só leitura)
                                                          │
                                       alloy (descobre os containers do projeto e lê os logs)
                                                          │
                                       loki (guarda 7 dias, volume lokidata) <── grafana (127.0.0.1:18184)
```

- **Alloy** (`grafana/alloy`) lê só os containers do projeto compose `chk3-poc-frame` com serviço de aplicação (canal, opcoes, cartao, integration, autorizacao, gateway-mock, worker). Rótulos: `project`, `service`, `level`, `category`. O resto (transactionId, passo...) é extraído na consulta, para não explodir a cardinalidade.
- **docker-proxy** (`tecnativa/docker-socket-proxy`): o Alloy não monta o `docker.sock`. O proxy monta o socket `:ro` e libera só `GET` de containers e redes (`CONTAINERS=1`, `NETWORKS=1`, `POST=0`). Fica na rede interna `obs`. Risco residual: quem controla o Alloy lê os logs e a configuração (env) de todos os containers do host. Nenhuma porta publicada.
- **Loki** (filesystem, TSDB, retenção 168 h). Os logs agora **sobrevivem ao deploy** (antes sumiam com o container).
- **Grafana** com datasource e dashboard provisionados do git (`observabilidade/grafana/provisioning/`). Sem plugins extras, sem cadastro, sem acesso anônimo.
- Os logs no Docker passaram a rodar com rotação (`json-file`, 10 MB × 3 por container).

## Acesso (só 127.0.0.1)

A porta é `${HOST_BIND:-127.0.0.1}:${GRAFANA_HOST_PORT:-18184}`. Na VPS, abra um túnel SSH:

```bash
ssh -L 18184:127.0.0.1:18184 vps      # e abra http://localhost:18184
```

- Usuário: `GRAFANA_ADMIN_USER` (`admin`). Senha: `GRAFANA_ADMIN_PASSWORD` do `.env` da VPS (gerada; ver com `grep GRAFANA_ADMIN_PASSWORD .env` na VPS, não copiar para chat/commit).
- A senha só vale na primeira subida do volume `grafanadata`. Para trocar depois: `docker compose exec grafana grafana cli admin reset-admin-password '<nova>'`.
- No box/local: `http://localhost:18184`.

## Dashboard "CHK3 · Transação ponta a ponta" (uid `chk3-transacao`)

Variáveis no topo: **transactionId** (cole o id) e **Pedido (orderId)** (filtra a lista de transações recentes).
Link direto: `http://localhost:18184/d/chk3-transacao/x?var-transactionId=<id>&from=now-6h&to=now`.

| Painel | O que mostra |
|---|---|
| Resultado final por status | contagem por `resultado` do passo 9-14 (Autorizada, Recusada, Indeterminada, 9a...) no período |
| Webhooks ao canal | entregas do passo 16 por status HTTP (200, 429, ...) |
| **Linha do tempo da transação** | uma linha por passo `chk3` do transactionId, em ordem: hora, passo, serviço, resultado, status, duração (ms), orderId, localRef. Do 1-4 (canal) ao 16 (webhook) |
| Transações recentes | passo 1-4 do canal; clique no transactionId para abrir a linha do tempo |
| **Gateway: ida (11) e volta (12)** | XML enviado ao Gateway e resposta, **mascarados**, com destino (`mock`/`real`), status HTTP, Content-Type e duração |
| Latência por passo e serviço | p95 de `duracaoMs` no período |
| Todos os logs com o transactionId | inclusive avisos (ex.: timeout do Gateway) |
| Erros e avisos | série por serviço e lista (todos os serviços) |

Screenshot de exemplo (VPS, 27/11/2026, transação aprovada do smoke): `poc-frame-docs/grafana-transacao-vps-2026-11-27.png`.

Consulta equivalente em Explore (Loki):

```
{project="chk3-poc-frame"} |= "chk3 passo=" |= "transactionId=<id>"
  | json passo="State.passo", servico="State.servico", resultado="State.resultado", duracaoMs="State.duracaoMs"
```

## Memória

Limites no compose: loki 384 MB (`GOMEMLIMIT` 320 MiB), alloy 256 MB (200 MiB), grafana 256 MB, docker-proxy 64 MB.
Medido na VPS após o deploy (27/11): loki ~75-145 MB, alloy ~70-186 MB (pico na leitura inicial dos logs), grafana ~90-180 MB, proxy ~34 MB. A VPS tem 15,6 GB e ficou com ~3,4 GB disponíveis.

Para desligar só a observabilidade: `docker compose stop grafana alloy loki docker-proxy` (o app continua).

## Mensagens do Gateway (desde 27/11, tarde)

Painel **"Gateway: ida (passo 11) e volta (passo 12)"**, logo abaixo da linha do tempo. Em Explore:

```
{project="chk3-poc-frame", service="autorizacao", category="chk3.gateway"} |= "<transactionId>"
  | json passo="State.passo", direcao="State.direcao", destino="State.destino", status="State.status", xml="State.xml"
  | line_format "passo {{.passo}} {{.direcao}} → {{.destino}} http={{.status}}\n{{.xml}}"
```

Só as chamadas ao Gateway real: acrescente `| destino="real"`. Screenshot: `poc-frame-docs/grafana-gateway-real-vps-2026-11-27.png`.

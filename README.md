# POC CHK3: fluxo de geração do frame (v7)

POC executável do **Frame V0 v7** do MCN Checkout 3.0, passos **1 a 14** de
`v0-ajustes/frame-v0-2026-11-24-v7.mmd` e `frame-v0-2026-11-24-v7-explicacoes.md`.

**RASCUNHO / POC.** Só cartões de teste. Não é contrato do Gateway MCN e não é ambiente PCI.

Stack: .NET 10 (ASP.NET Core minimal APIs), Postgres 17, Npgsql + Dapper, docker compose.
Sem Front Door, APIM, Identity Server, Firewall, VPN e Worker.

## Pré-requisitos
- **Docker** com **compose v2** (`docker compose version`).
  - Linux: Docker Engine.
  - macOS e Windows: Docker Desktop. No Windows, rode os scripts no WSL2 ou no Git Bash.
- Para os scripts: `bash`, `curl`, `openssl` e `jq`.
  - `init-env.sh` funciona sem `openssl`, usando `/dev/urandom`.
  - `test.sh` só precisa de Docker.
- Opcional, para o E2E no navegador: Node 18+ e Google Chrome.
- Não precisa de SDK .NET no host. O build e os testes rodam nos containers oficiais (`mcr.microsoft.com/dotnet/sdk:10.0`).

## Quickstart (clone limpo)

```bash
git clone https://github.com/DanielMOliveira/chk3-poc-frame.git
cd chk3-poc-frame
./scripts/init-env.sh             # cria o .env com segredos aleatórios (nunca é commitado)
docker compose up -d --build
./scripts/smoke.sh                # smoke ponta a ponta: aprova, recusa e timeout
./scripts/test.sh                 # testes xUnit num container do SDK, dentro da rede do compose
```

Demo no navegador: abrir `http://localhost:8080` (ou a `CANAL_HOST_PORT`/`CANAL_PUBLIC_ORIGIN` escolhida) e clicar em Pagar.

Portas diferentes, por exemplo se a 8080 estiver ocupada:

```bash
CANAL_HOST_PORT=19180 OPCOES_HOST_PORT=19181 CARTAO_HOST_PORT=19182 INTEGRATION_HOST_PORT=19183 \
  ./scripts/init-env.sh --force
docker compose up -d --build
```

Também dá para editar as portas direto no `.env`. As origens (CSP, `frame-ancestors`, `frameUrl`) acompanham as portas automaticamente quando as variáveis `*_PUBLIC_ORIGIN` ficam vazias.

| Cartão de teste       | Resultado no mock                         |
|-----------------------|-------------------------------------------|
| 4111 1111 1111 1111   | aprova (visa)                             |
| 5555 5555 5555 4444   | aprova (mastercard)                       |
| 4000 0000 0000 0002   | recusa (visa)                             |
| 5105 1051 0510 5100   | recusa (mastercard)                       |
| 4000 0000 0000 0119   | não responde no prazo, vira indeterminado |

Use validade futura e CVV `123`. Qualquer outro PAN é recusado, mesmo com Luhn válido. A API Autorização e o mock só aceitam essa lista de teste.

E2E opcional em navegador real: `npm install && npm run e2e`.
- Usa o Google Chrome instalado. Para outro binário, defina `CHROME=/caminho`.
- Valida CSP, `frame-ancestors`, iframe aninhado e `postMessage`.

Parar:
- `docker compose down` mantém a base;
- `docker compose down -v` apaga a base.

## Variáveis do `.env`

| Variável | Padrão | Para que serve |
|---|---|---|
| `POSTGRES_USER`, `POSTGRES_DB` | `chk3`, `chk3poc` | Base Postgres |
| `POSTGRES_PASSWORD` | gerado | Senha da base (a base não tem porta publicada) |
| `CHANNEL_JWT_SECRET` | gerado | Segredo HS256 do token do canal (mínimo 32 bytes) |
| `CHANNEL_JWT_ISSUER` | `chk3-poc-issuer` | `iss` esperado no token do canal |
| `INTERNAL_API_KEY` | gerado | Chave dos endpoints internos (no lugar da Managed Identity) |
| `SNAPSHOT_SIGNING_KEY` | gerado | HMAC do snapshot de bandeiras (mínimo 32 bytes) |
| `GATEWAY_API_KEY` | gerado | Chave do mock do Gateway |
| `HOST_BIND` | `127.0.0.1` | Interface das portas publicadas. `127.0.0.1` = só a máquina. `0.0.0.0` = todas as interfaces (rede local, sem TLS: evitar) |
| `CANAL_HOST_PORT` | `8080` | Porta do canal fake no host |
| `OPCOES_HOST_PORT` | `8081` | Porta da Tela de opções no host |
| `CARTAO_HOST_PORT` | `8082` | Porta da Web App Cartão no host |
| `INTEGRATION_HOST_PORT` | `8083` | Porta da Integration API (`POST /sessions`) no host |
| `CANAL_PUBLIC_ORIGIN` | `http://localhost:${CANAL_HOST_PORT}` | Origem que o navegador vê. Entra na CSP e no `postMessage` |
| `OPCOES_PUBLIC_ORIGIN` | `http://localhost:${OPCOES_HOST_PORT}` | Origem da Tela. Entra na `frameUrl`, na CSP e no `postMessage` |
| `CARTAO_PUBLIC_ORIGIN` | `http://localhost:${CARTAO_HOST_PORT}` | Origem do endpoint pay. Entra no `frame-src`/`form-action` da Tela |
| `GATEWAY_TIMEOUT_MS` | `3000` | Timeout da API Autorização para o Gateway (12b) |
| `GATEWAY_MOCK_TIMEOUT_DELAY_MS` | `10000` | Atraso do cartão de timeout no mock |

Regra: com as origens vazias ou comentadas, elas derivam das portas. Atrás de proxy ou túnel com domínio, defina as três origens com o endereço público, sem barra no fim. O `docker-compose.yml`, o `smoke.sh`, o `browser-e2e.mjs` e os testes usam a mesma regra.

## VPS atrás de túnel HTTPS (ex.: Cloudflare Tunnel)

Só com o `.env`, sem `docker-compose.override.yml`:

```dotenv
HOST_BIND=127.0.0.1
CANAL_HOST_PORT=18180
OPCOES_HOST_PORT=18181
CARTAO_HOST_PORT=18182
INTEGRATION_HOST_PORT=18183
CANAL_PUBLIC_ORIGIN=https://canal.seu-dominio
OPCOES_PUBLIC_ORIGIN=https://checkout.seu-dominio
CARTAO_PUBLIC_ORIGIN=https://pay.seu-dominio
```

O túnel publica cada hostname no serviço local correspondente:
- `canal.seu-dominio` → `http://127.0.0.1:18180`
- `checkout.seu-dominio` → `http://127.0.0.1:18181`
- `pay.seu-dominio` → `http://127.0.0.1:18182`

A porta 18183 (`/sessions`) só precisa ir para o túnel se um canal externo for chamar a API. O canal fake a chama pela rede interna do compose.

Use três hostnames distintos. Opções e Cartão precisam ser origens diferentes, e o túnel não pode cachear essas páginas, que já respondem `no-store`.

Na VPS:
- `./scripts/smoke.sh` usa as origens HTTPS e chama a Integration em `http://127.0.0.1:18183`;
- `./scripts/test.sh` roda dentro da rede do compose.

## Arquitetura e mapeamento para o v7

| Serviço compose | Componente v7 | Porta host | Rede(s) | Passos |
|---|---|---|---|---|
| `canal` | Canal Workflow (fake) | `CANAL_HOST_PORT` (8080) | fora | 1, 4 (chama `POST /sessions` do servidor e embute o iframe) |
| `opcoes` | Tela de opções (fora da CDE) | `OPCOES_HOST_PORT` (8081) | fora | 5, 6, 7, 14 |
| `cartao` | Web App Cartão (CDE, endpoint pay) | `CARTAO_HOST_PORT` (8082) | cde | 8, 9, 14 |
| `autorizacao` | API Autorização (CDE, sem banco) | — | cde, vpn | 9 a 13 |
| `integration` | Integration API | `INTEGRATION_HOST_PORT` (8083, só `/sessions`) | fora, cde, dados | 2, 3, 4, 6, 7, 8, 10, 13 |
| `gateway-mock` | Gateway MCN | — | vpn (internal) | 11, 12 |
| `postgres` | SQL | — | dados (internal) | 3, 6, 7, 10, 13 |

Fluxo:

1. **1–4** `POST /sessions` com JWT do canal (HS256, `scope=sessions:create`, `sub`=canal) e `Idempotency-Key`.
   - **2a**: `camposAdicionais` fora dos limites ou com padrão de PAN dá 400, sem gravar nem logar.
   - **2b**: parcelas acima do máximo do canal dá 400.
   - **3**: TTL lido do cadastro do canal (`channels.session_ttl_seconds`, padrão 1800 s, máximo 86400 s).
   - **3a**: mesma chave com outro conteúdo dá 409.
   - **3b**: mesma chave e mesmo conteúdo devolvem a mesma sessão com uma credencial nova e o mesmo TTL. Se a sessão expirou ou já tem tentativa, devolve só o estado.
   - **3c**: cria a sessão e grava só o SHA-256 da credencial (32 bytes aleatórios, prefixo `pc_`).
   - **4**: devolve `frameUrl = <OPCOES>/#pc=<credencial>`. A credencial vai no fragment, nunca na query. O valor nunca vai na URL.
2. **5–6** A Tela de opções lê `location.hash`, guarda a credencial só em memória e limpa o hash (`history.replaceState`).
   - Ela manda a credencial no header `X-Payment-Credential`. Não usa cookie nem storage.
   - A Integration valida **sem consumir**.
   - O frame pode ser reapresentado dentro do TTL: o botão "Recarregar frame" do canal reaplica a mesma URL, e o 3b dá uma credencial nova.
3. **7** O usuário escolhe cartão e parcelas.
   - A Integration revalida e emite a credencial de digitação (`tc_`, hash gravado, expira com a sessão) mais o snapshot de bandeiras assinado (HMAC-SHA256).
4. **8** A Tela faz um POST de `tc` para o iframe filho `cartao/digitacao`.
   - A Web App Cartão valida a credencial sem consumir e recebe o snapshot assinado.
   - O script fake de antifraude gera o `deviceSessionId` opcional.
5. **9** O navegador envia PAN, validade, CVV, parcelas, snapshot e `deviceSessionId` à Web App Cartão (header `X-Typing-Credential`), que repassa à API Autorização. **Nunca envia o valor.**
   - A API Autorização verifica a assinatura e a expiração do snapshot e revalida Luhn, validade, CVV, bandeira e parcelas.
   - **9a**: erro de validação, sem tentativa registrada. Há limite de 5 falhas por credencial (card testing, v7 §3.4).
6. **10** Tentativa única atômica na Integration (lock na sessão + PK em `attempts`).
   - **10a**: sessão inválida ou expirada, snapshot de outra sessão, bandeira ou parcelas fora do cadastro.
   - **10b**: tentativa já registrada, devolve o estado sem novo envio.
   - **10c**: estado `AutorizacaoSolicitada` e devolve valor, moeda, parcelas, pedido e referência local do servidor.
7. **11–12** A API Autorização chama o mock com o valor do servidor.
   - **12b**: timeout (`GATEWAY_TIMEOUT_MS`, 3 s) vira `Indeterminada`, sem reenvio.
   - O PAN é descartado.
8. **13** A Integration grava, de forma atômica, o estado `Autorizada`/`Recusada`/`Indeterminada`, o código sanitizado e a linha no `outbox`.
9. **14** A Web App Cartão faz `postMessage` para a Tela de opções (origem restrita), que mostra a mensagem e avisa o canal por `postMessage`. Esse aviso **não prova o pagamento**.

### Segurança aplicada (OWASP)
- **Token do canal** só no servidor do canal fake. O navegador nunca o recebe.
- **Credenciais**:
  - opacas, aleatórias, só o hash na base;
  - enviadas em header e nunca em query: qualquer query string é recusada com 400 em Opções, Cartão e Integration;
  - comparação de chaves em tempo constante.
- **CSP** por app:
  - `frame-ancestors`: Opções só aceita o canal; Cartão aceita Opções e o canal, porque `frame-ancestors` verifica todos os ancestrais.
  - `script-src 'self'`, sem inline, `form-action` e `frame-src` restritos.
  - Também: `Cache-Control: no-store`, `Referrer-Policy: no-referrer` e `nosniff`.
- **PAN**:
  - só em memória na Web App Cartão e na API Autorização;
  - nunca logado nem persistido, nunca em fila;
  - logs do ASP.NET em `Warning`.
  - Verificado: `docker compose logs` não contém PAN, credenciais nem `deviceSessionId`.
- **Endpoints internos** (passos 6–13) só na porta 8090 da Integration, que não é publicada, e com `X-Internal-Key`. Na porta pública, `/internal` dá 404.
- **Redes**:
  - `vpn` (API Autorização ↔ mock) e `dados` (Integration ↔ Postgres) são `internal`;
  - Postgres sem porta publicada;
  - portas publicadas só em `127.0.0.1` por padrão (`HOST_BIND`).
- **Containers**: usuário não root, `read_only`, `no-new-privileges`.
- **Integration**: rate limit por canal (120/min) e limite de tamanho de corpo.

## Testes

`tests/Chk3.Tests` (xUnit). São 37 casos:
- **unitários**: Luhn, bandeira, padrão de PAN, snapshot adulterado ou expirado;
- **integração por HTTP contra a stack**:
  - replay de idempotência, 409 e criação concorrente;
  - credencial inválida, expirada (canal `canal-ttl-curto`, TTL 3 s) e trocada de tipo;
  - credencial na query string recusada;
  - JWT inválido, expirado, sem escopo ou de canal desconhecido;
  - 2a e 2b;
  - porta interna;
  - cabeçalhos CSP;
  - aprova, recusa e timeout pelo mock;
  - tentativa única, inclusive com 5 envios concorrentes e com duas credenciais de digitação;
  - 9a sem tentativa registrada;
  - snapshot de outra sessão;
  - bandeira fora do snapshot;
  - limite de falhas.

## Desvios em relação ao v7 (POC)
- **Borda e identidade.** Sem Front Door, APIM e Identity Server. O JWT do canal é estático, HS256, com segredo no `.env`. Rate limit e cotas ficam na própria Integration.
- **Rede interna.** Sem Managed Identity, Firewall HubPCI3.0 e VPN S2S. Os endpoints internos usam uma chave compartilhada (`INTERNAL_API_KEY`) e redes docker separadas.
- **Base de dados.** Pagamento e sessão estão numa tabela só (`payment_sessions`). Não há projeção de consulta: o passo 13 grava estado, evidência e outbox.
- **"Sessão sem tentativa concluída" (6 e 3b).** O POC fecha o frame assim que existe **qualquer** tentativa registrada, mesmo em andamento. É mais restritivo que o texto do v7.
- **Recarregar.** A credencial não sobrevive a um F5 dentro do iframe, porque fica só em memória e o hash é limpo. A reapresentação no TTL é feita pelo canal, reaplicando a mesma `frameUrl`, ou pelo 3b.
- **Parcelas.** Escolhidas na Tela (7), levadas no POST do passo 8, enviadas no 9 e validadas no 10 contra o cadastro. Segue o v7, que mantém o envio no 9. A proposta de gravar no 7 está pendente.
- **Antifraude.** O `deviceSessionId` é gerado por um script local fake. O provedor real não está integrado.
- **TTL mínimo.** O cadastro aceita a partir de 1 s, só para testar a expiração (canal `canal-ttl-curto`). Em produção, definir um mínimo.
- **Card testing.** O limite de falhas é por credencial e fica em memória na Web App Cartão (uma réplica).
- **HTTP local.** Na VPS, **HTTPS é obrigatório** (proxy TLS na frente). Sem TLS, a credencial no fragment trafega em claro dentro da página do canal.

## Fora do escopo
- Worker, reconciliação de `Indeterminada` e webhook ao canal (passos 15–16). O `outbox` é gravado mas não é entregue.
- Consulta, captura e cancelamento.
- Google Pay, 3DS, banco na CDE e Key Vault/HSM.
- Contrato real do Gateway MCN.

## Pontos em aberto (TODO, ver v7 §4)
- **Contrato do Gateway**: campos, códigos de recusa, timeout, campo do `deviceSessionId` e dado de cartão na resposta 12. O contrato do mock (`/poc/v1/authorize`) é **inventado**.
- **Credencial**: nome do header (POC: `X-Payment-Credential` / `X-Typing-Credential`), formato e tamanho (POC: prefixo + 32 bytes base64url) e TTL da credencial de digitação (POC: igual ao da sessão).
- **Credenciais anteriores no 3b**: revogar ou manter. O POC mantém válidas até o TTL.
- **Antifraude**: provedor e obrigatoriedade por canal.
- **Fonte das parcelas**: passo 7 ou 9.
- **Banco na CDE.**

## Estrutura
```
src/Shared        helpers: Luhn/bandeira/padrão de PAN, snapshot assinado, CSP, recusa de query, env
src/Integration   Integration API + migrações SQL (Migrations/*.sql, aplicadas no arranque)
src/Opcoes        Tela de opções (wwwroot/opcoes.js)
src/Cartao        Web App Cartão (wwwroot/digitacao.js, antifraude-fake.js)
src/Autorizacao   API Autorização
src/GatewayMock   mock do Gateway
src/CanalFake     canal fake
tests/Chk3.Tests  testes
scripts/          init-env.sh, smoke.sh, test.sh, lib-env.sh, browser-e2e.mjs
```

## Deploy
Nada é publicado por este repo. O deploy na VPS será feito via git, coordenado com o agente Monitor Casa:
`.env` próprio da VPS (nunca versionado, gerado com `init-env.sh`), portas e origens HTTPS no `.env` (ver seção da VPS).

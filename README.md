# POC CHK3: fluxo de geração do frame (v7)

POC executável do **Frame V0 v7** do MCN Checkout 3.0, passos **1 a 14** de
`v0-ajustes/frame-v0-2026-11-24-v7.mmd` e `frame-v0-2026-11-24-v7-explicacoes.md`.

**RASCUNHO / POC.** Só cartões de teste. Não é contrato do Gateway MCN e não é ambiente PCI.

**Referência visual do frame:** o modelo do Daniel, "Checkout · Multipagamentos", em
[`docs/modelo-visual/checkout-multipagamentos.html`](docs/modelo-visual/checkout-multipagamentos.html).
Ele vale como modelo do frame daqui em diante. Ver a seção "Modelo visual" para o que foi aplicado e os desvios.

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

E2E opcional em navegador real: `npm install && npm run e2e`. Com `SHOTS_DIR=/pasta`, salva screenshots das telas.
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

## Modos de acesso fora da máquina local

As portas do host e as origens públicas são independentes:
- `*_HOST_PORT` é onde a stack escuta, na máquina que roda o compose.
- `*_PUBLIC_ORIGIN` é o endereço que o **navegador** usa.

O smoke fala direto com as portas do host (`127.0.0.1:*_HOST_PORT`), seja qual for a origem pública.

### Modo A: túnel SSH (o atual da VPS)

Na VPS, com o `.env` abaixo, sem `docker-compose.override.yml`:

```dotenv
HOST_BIND=127.0.0.1
CANAL_HOST_PORT=18180
OPCOES_HOST_PORT=18181
CARTAO_HOST_PORT=18182
INTEGRATION_HOST_PORT=18183
CANAL_PUBLIC_ORIGIN=http://localhost:8080
OPCOES_PUBLIC_ORIGIN=http://localhost:8081
CARTAO_PUBLIC_ORIGIN=http://localhost:8082
```

No PC de quem testa, abrir o túnel e depois acessar `http://localhost:8080`:

```bash
ssh -N -L 8080:127.0.0.1:18180 -L 8081:127.0.0.1:18181 -L 8082:127.0.0.1:18182 -L 8083:127.0.0.1:18183 <usuario>@<vps>
```

- As portas **locais** do PC (8080 a 8082) precisam ser iguais às das origens públicas, porque a CSP e o `frame-ancestors` comparam a origem exata.
- Se 8080 a 8082 estiverem ocupadas no PC, troque as portas locais do túnel **e** as `*_PUBLIC_ORIGIN` do `.env` da VPS, e rode o deploy de novo.
- `localhost` é contexto seguro no navegador, mesmo em HTTP.
- A porta 8083 (`POST /sessions`) só é necessária para chamar a API direto do PC.

### Modo B: HTTPS atrás de proxy ou túnel (ex.: Cloudflare Tunnel, Nginx)

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

- Cada hostname aponta para a porta local correspondente (`canal` → `127.0.0.1:18180`, `checkout` → `:18181`, `pay` → `:18182`). Use três hostnames distintos.
- Se houver um proxy de autenticação na frente (ex.: Cloudflare Access), os três hostnames precisam estar autorizados para o usuário.
- O JS só chama a **própria origem**, com `credentials: 'same-origin'`, então o cookie do proxy segue junto. Antes, o `credentials: 'omit'` causava o "Carregando..." infinito.
- A primeira navegação ao endpoint pay é um POST num iframe. Se o proxy redirecionar esse POST para o login, ele se perde, e a Tela mostra o erro "Não conseguimos abrir a digitação do cartão".
- Essa limitação é do proxy, não do POC: autentique os três hostnames antes, abrindo cada um uma vez.
- Login de proxy dentro de iframe também esbarra na CSP (`frame-src`) e em cookies de terceiros. O modo A não tem esse problema.

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
3. **7** O usuário escolhe "Cartão de crédito" (as parcelas ficam no iframe do cartão, como no modelo visual).
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

## Modelo visual (referência: `docs/modelo-visual/checkout-multipagamentos.html`)

Aplicado ao POC inteiro, mantendo a arquitetura:
- **Base comum:** tokens de cor, gradiente roxo, tipografia Helvetica Neue, raios, sombras, campos, botões e estados.
  - Fica em `src/Shared/design/mp-base.css`, copiado para o `wwwroot` de cada app no build.
- **Canal fake:** fundo e cartões do modelo. A loja embute o frame.
- **Tela de opções:** é o `.app` do modelo.
  - Header com gradiente, marca, "Total a pagar", loja e pedido, e "Ambiente seguro".
  - Botão Voltar.
  - Telas com transição: carregando, opções, cartão, processando, aprovado, recusado, em confirmação (indeterminado) e erro/expirado.
- **Web App Cartão (iframe filho, CDE):** os campos do modelo (número com bandeira, nome, validade, CVV, parcelamento e "Pagar R$ X"). O PAN existe **só** nesse iframe.

Segurança mantida:
- CSP sem `unsafe-inline` (scripts **e** estilos): todo CSS e JS está em arquivos estáticos.
- Os `style="..."` e `onclick` do modelo viraram classes e `addEventListener`.
- Alturas dinâmicas usam CSSOM, que a CSP permite.
- Sem CDN e sem imagem externa: o logo NuPay do modelo vinha de `logospng.org`; ficou o fallback local.

Desvios do modelo:
- **"Em breve" (desabilitados, sem comportamento simulado):** Google Pay, Apple Pay, Click to Pay, NuPay, Dois cartões de crédito, Visa Pix · Biometria, Pix · Open Finance e Pix · Copia e Cola. As telas internas desses meios no modelo (carteira, QR, bancos, OTP, divisão de valor) não foram implementadas.
- **Marcas:** sem os SVGs oficiais (marcas registradas, pasta `brands/` do modelo). Usei os fallbacks neutros do próprio modelo, convertidos em CSS.
- **Textos sem promessa falsa:** sem "3DS" e sem "tokenização Card on File". O subtítulo do cartão diz que os dados são digitados num ambiente isolado. O status "Confirmado" virou "Autorizado", porque o retorno do frame não prova o pagamento final.
- **Parcelamento:** mostrado como "Nx de R$ Y", sem "sem juros/c/ juros" (regra de juros desconhecida). Fica dentro do iframe do cartão, como no modelo. É enviado no passo 9 e validado no 10.
- **Nome impresso:** campo exigido na UI como no modelo, mas **não enviado** (campo do Gateway a confirmar).
- **CVV com 3 dígitos:** Amex não é suportado no POC. A bandeira só aparece como rótulo.
- **Telas novas no mesmo estilo:** recusado, em confirmação (indeterminado), erro/expirado e falha de rede com "Tentar novamente". O botão "Voltar ao início" virou "Voltar à loja" (avisa o canal por `postMessage`), já que não há segunda tentativa na mesma sessão.

Falha visível:
- Toda chamada do navegador tem timeout (15 s na Tela, 25 s no pagamento) e `redirect: 'error'`.
- Falha de rede, redirect de proxy ou iframe do cartão que não sinaliza "pronto" a tempo mostram uma tela de erro, em vez de "Carregando..." infinito.

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

Rastreio e logs: `./scripts/logs-check.sh`. Também roda no `deploy.sh`, depois do smoke.
- Faz três transações pelo canal (aprovada, recusada e indeterminada).
- Falha se aparecer dado sensível nos logs (PAN, CVV, validade, nome, `pc_`/`tc_`, fragment, JWT, snapshot, hash ou segredos do `.env`).
- Falha se o `transactionId` não aparecer em todos os serviços, com os passos esperados.
- Ver [`docs/operacao/logs-e-rastreio.md`](docs/operacao/logs-e-rastreio.md): log JSON, linha `chk3` por passo e `X-Correlation-Id`.

## Desvios em relação ao v7 (POC)
- **Borda e identidade.** Sem Front Door, APIM e Identity Server. O JWT do canal é estático, HS256, com segredo no `.env`. Rate limit e cotas ficam na própria Integration.
- **Rede interna.** Sem Managed Identity, Firewall HubPCI3.0 e VPN S2S. Os endpoints internos usam uma chave compartilhada (`INTERNAL_API_KEY`) e redes docker separadas.
- **Base de dados.** Pagamento e sessão estão numa tabela só (`payment_sessions`). Não há projeção de consulta: o passo 13 grava estado, evidência e outbox.
- **"Sessão sem tentativa concluída" (6 e 3b).** O POC fecha o frame assim que existe **qualquer** tentativa registrada, mesmo em andamento. É mais restritivo que o texto do v7.
- **Recarregar.** A credencial não sobrevive a um F5 dentro do iframe, porque fica só em memória e o hash é limpo. A reapresentação no TTL é feita pelo canal, reaplicando a mesma `frameUrl`, ou pelo 3b.
- **Parcelas.** Escolhidas na página de digitação (8/9, como no modelo visual, e não na Tela no passo 7), enviadas no 9 e validadas no 10 contra o cadastro. O v7 mantém o envio no 9. A proposta de gravar no 7 está pendente.
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
scripts/          init-env.sh, smoke.sh, logs-check.sh, test.sh, lib-env.sh, browser-e2e.mjs, deploy.sh, deploy-remote.sh
docs/modelo-visual/ modelo visual de referência (HTML do Daniel)
docs/operacao/    logs-e-rastreio.md: containers, logs, SQL e rastreio de transação
```

## Deploy via git

`scripts/deploy.sh [ref]` roda **na máquina alvo**, dentro do clone. O ref padrão é `origin/main`. Ele:
1. recusa rodar sem `.env`, com `docker-compose.override.yml` presente (use `ALLOW_OVERRIDE=1` para forçar) ou com alterações locais em arquivos versionados;
2. faz `git fetch` e `git checkout --detach <sha do ref>`;
3. faz `docker compose up -d --build --remove-orphans`;
4. espera o `/health` da Integration;
5. roda o `./scripts/smoke.sh`.

Qualquer falha encerra com `DEPLOY FALHOU: ...`, código ≠ 0, `ps` e logs.

De outra máquina, por SSH:

```bash
scripts/deploy-remote.sh <ssh-host> <dir-do-app> [ref]
# ex.: scripts/deploy-remote.sh vps /home/daniel/projetos/chk3-poc-frame origin/main
```

O `deploy-remote.sh` faz o fetch no alvo e executa o `deploy.sh` **da versão do ref**, então o próprio script também é atualizado.

**Credencial do git:** o repositório é público, e o remote da VPS é HTTPS sem token (`https://github.com/DanielMOliveira/chk3-poc-frame.git`). O fetch não precisa de credencial. Se o repo ficar privado:
- exporte `DEPLOY_GIT_TOKEN` (token só de leitura) na máquina que dispara o deploy;
- o `deploy-remote.sh` envia o token por **stdin** ao ambiente do deploy remoto, que o usa só num `git -c credential.helper=... fetch`;
- o token não vai para a linha de comando, para o histórico, para o `git config` nem para a URL do remote.

Para acompanhar containers, logs e transações, ver [`docs/operacao/logs-e-rastreio.md`](docs/operacao/logs-e-rastreio.md).

Na VPS o app fica em `/home/daniel/projetos/chk3-poc-frame`. O nome do projeto compose é fixo (`name: chk3-poc-frame` no `docker-compose.yml`), então mover o diretório não troca o nome dos containers nem o volume do Postgres (`chk3-poc-frame_pgdata`).

O `.env` da VPS nunca é versionado. Gere com `init-env.sh` e ajuste portas e origens conforme o modo de acesso.

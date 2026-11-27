# Contratos da POC: POST /sessions (JSON), Gateway (XML), webhook e token do frame

Versão de 27/11/2026 (commits `13560fc`..`0ad9381`). **RASCUNHO / POC**: o XML vai só para o `gateway-mock`, nunca para um gateway real.

## 1. `POST /sessions` (canal → Integration, JSON)

`Content-Type: application/json`, `Authorization: Bearer <JWT do canal>`, `Idempotency-Key: <8..128 ASCII>`.

**Só `amount` é obrigatório.** O resto é opcional; o que faltar vem do cadastro do canal (`channels`) ou é gerado.

| Campo | Tipo | Obrigatório | Regra / padrão |
|---|---|---|---|
| `amount` | inteiro, centavos | **sim** | 1..1.000.000.000. Sem ele: 400 `valor_invalido` |
| `orderId` | string | não | `[A-Za-z0-9._-]{1,64}`. Se faltar, a Integration gera `CHK3-<20 hex>` e devolve na resposta |
| `currency` | string | não | só `BRL` (padrão) |
| `installments` | inteiro | não | **máximo** de parcelas que o comprador pode escolher (1..`channels.max_installments`, padrão 12). Acima: 400 `parcelas_invalidas` (2b) |
| `billing` | objeto | não | campos abaixo; cada um opcional |
| `urlReturn` | string | não | URL absoluta http/https, até 512. Erro: 400 `url_return_invalida` |
| `softDescriptor` | string | não | `[A-Za-z0-9 ]{1,13}`. Se faltar, usa `GATEWAY_SOFT_DESCRIPTOR` do `.env`. Erro: 400 `soft_descriptor_invalido` |
| `camposAdicionais` | objeto string→string | não | até 10 chaves `[A-Za-z0-9_]{1,40}`, sem padrão de PAN (2a) |

`billing` usa os mesmos nomes do XML. Campo inválido: 400 `billing_<campo>_invalido` (o valor nunca vai para o log).

| Campo | Regra |
|---|---|
| `customerIdentity` | `[0-9A-Za-z./-]{1,20}` (CPF/CNPJ) |
| `name` | letras, espaço, `.'-`, 2..100 |
| `address` / `address2` | até 120 / 60 |
| `city` | até 60 |
| `state` | UF, 2 maiúsculas |
| `postalCode` | 8 dígitos |
| `country` | 2 maiúsculas (no XML vira `BR` se faltar e houver algum dado de billing) |
| `phone` | 10..13 dígitos |
| `email` | e-mail simples |

Exemplo mínimo e completo:

```json
{"amount": 1990}
```

```json
{
  "amount": 1990,
  "orderId": "PED-1001",
  "installments": 6,
  "billing": {"customerIdentity": "12345678909", "name": "Jose da Silva", "address": "Av. Paulista, 1000",
              "city": "São Paulo", "state": "SP", "postalCode": "01310100", "country": "BR",
              "phone": "11999990000", "email": "jose@exemplo.com"},
  "urlReturn": "https://loja.exemplo/retorno",
  "softDescriptor": "SUALOJA",
  "camposAdicionais": {"origem": "canal-fake"}
}
```

Resposta 201: `transactionId`, `orderId` (o enviado ou o gerado), `maxInstallments`, `state`, `expiresAt`, `frameUrl` (credencial no fragment).
O `Idempotency-Key` cobre todos os campos: mesma chave com outro conteúdo dá 409 (3a).

**No canal fake** (`http://localhost:8080`): só "Valor (centavos)" é obrigatório. Pedido, parcelas máximas, billing (em "Dados opcionais"), urlReturn e softDescriptor são opcionais. A página mostra o **JSON enviado** no `POST /sessions`.

## 2. Parcelas

- O número da criação (ou o padrão do canal, até 12) é o **máximo**. O iframe do cartão mostra um dropdown `1..máximo` (limitado também ao valor: nunca parcela menor que R$ 0,01).
- Regra BR, em centavos: `parcela = floor(total / n)`; o resto vai na **primeira**. Sem juros no V0.
  - Ex.: R$ 19,90 em 3x → `3x de R$ 6,63 sem juros (1ª de R$ 6,64)`.
- O servidor valida `1 <= n <= máximo` na API Autorização (9a, 422 `parcelas_invalidas`) e de novo na Integration (10a).
- Código: `src/Shared/Installments.cs`.

## 3. Gateway: `<transaction-request>` (passo 11, API Autorização → gateway-mock)

`POST /poc/v1/authorize`, `Content-Type: application/xml`. O XML é montado **na API Autorização (CDE)**, porque o PAN só existe lá (v7, passos 9 e 11). A Integration entrega os dados sem cartão (pedido, billing, urlReturn, softDescriptor) na resposta do passo 10c.

```xml
<transaction-request>
  <version>1.0.0</version>
  <verification>
    <merchantId>GATEWAY_MERCHANT_ID</merchantId>
    <merchantKey>GATEWAY_MERCHANT_KEY (só no .env)</merchantKey>
    <environment>GATEWAY_ENVIRONMENT (padrão 1; vazio = omitido)</environment>
  </verification>
  <authorize>
    <order><reference>orderId</reference><totalAmount>centavos</totalAmount></order>
    <payment>
      <acquirer>GATEWAY_ACQUIRER: 1=CIELO, 20=STONE, 22=GETNET</acquirer>
      <method>1 = crédito à vista; parcelado = GATEWAY_METHOD_PARCELADO (PENDENTE)</method>
      <amount>centavos</amount><currency>986</currency><country>BRA</country>
      <numberOfPayments>n</numberOfPayments><groupNumber>0</groupNumber>
      <flag>visa|mastercard (pelo BIN)</flag><cardHolder>nome</cardHolder>
      <cardNumber>PAN</cardNumber><cardSecurityCode>CVV</cardSecurityCode><cardExpirationDate>YYYYMM</cardExpirationDate>
      <saveCreditCard>GATEWAY_SAVE_CREDIT_CARD</saveCreditCard><generateToken>false</generateToken>
      <departureTax>0</departureTax><softDescriptor>da sessão ou GATEWAY_SOFT_DESCRIPTOR</softDescriptor>
    </payment>
    <billing>só os campos informados, na ordem: customerIdentity, name, address, address2, city, state, postalCode, country, phone, email</billing>
    <urlReturn>da sessão ou vazio</urlReturn>
    <fraud>false</fraud>
    <customField />
  </authorize>
</transaction-request>
```

- Código: `src/Autorizacao/GatewayXml.cs`. Teste de forma contra o modelo: `tests/Chk3.Tests/GatewayXmlTests.cs` (valores fictícios).
- **O mock** (`src/GatewayMock/Program.cs`) exige `application/xml` (senão 415), lê com parser seguro (sem DTD/entidades externas, até 64 KB), confere ordem e valor de cada elemento e o merchant (comparação em tempo constante). Erro de merchant: 401; erro de formato: 400 `XML_INVALIDO` com o caminho do campo. O log mostra só `xml_invalido_<campo>` ou `xml_ok_n<parcelas>_m<method>_<resultado>`.
- **Resposta do mock** (formato inventado no POC, o real não foi informado): `<transaction-response><status>approved|declined</status><code>00|05</code></transaction-response>`.
- **Nunca no log:** XML bruto, PAN, CVV, validade, nome, merchantKey. O `scripts/logs-check.sh` procura tags XML, os dados de billing de teste e os segredos do `.env` em todos os logs.

**Pendências do XML**
- `method` do crédito parcelado: o modelo só traz `1` (à vista). O POC usa `GATEWAY_METHOD_PARCELADO=2`, **provisório, não é fato**.
- `flag`: detectada pelo BIN só para visa/mastercard (lista de teste). Falta a tabela de BIN/bandeiras real.
- `saveCreditCard`: `false` por padrão (o modelo trazia `true`); configurável.
- `deviceSessionId` do antifraude: o XML não tem campo; não é enviado (o `<fraud>` vai `false`).
- Formato da resposta do Gateway: não informado; o do mock é inventado.

## 4. Webhook do Worker (passo 16)

- O Worker lê o outbox (`FOR UPDATE SKIP LOCKED`) e faz `POST` JSON para `channels.webhook_url` ou, se vazio, `WEBHOOK_DEFAULT_URL` do `.env`. Só https.
- Na VPS: `WEBHOOK_DEFAULT_URL=https://webhook.site/<id>` (no `.env`, não no repo, porque o repo é público).
- Payload (sem PAN, CVV, validade, nome, billing ou credenciais):

```json
{"eventId":"uuid","eventType":"pagamento.autorizada","occurredAt":"2026-11-27T00:31:02.55Z",
 "transactionId":"01a21a69-...","orderId":"PED-1001","state":"Autorizada","amount":1990,"currency":"BRL",
 "installments":3,"camposAdicionais":{"origem":"canal-fake"}}
```

- Headers: `X-Chk3-Event-Id`, `X-Chk3-Event-Type` e `X-Chk3-Signature: t=<unix>,v1=<hex>`, onde `v1 = HMAC-SHA256(WEBHOOK_SIGNING_SECRET, "<t>.<corpo>")`. O canal verifica o HMAC e recusa `t` antigo (ex.: > 5 min).
- Retentativa: até `WEBHOOK_MAX_ATTEMPTS` (8), espera `5 s × 2^(n-1)` (máx. 1 h). Log: `passo=16 resultado=webhook_entregue_<host>_http<status>_tentativa<n>` ou `webhook_falhou_...`. A URL completa não vai para o log.
- Eventos do outbox anteriores ao Worker ficaram marcados como não enviados (`anterior_ao_worker_nao_enviado`) para não inundar o destino.
- O webhook é o resultado autoritativo; o `postMessage` do frame não é prova de pagamento.

## 5. Token do frame (`frame_tokens`) e histórico (CHK-5)

Migração `src/Integration/Migrations/0002_token_frame_parcelas_webhook.sql`, aplicada sobre o volume `chk3-poc-frame_pgdata` sem recriar a base (renomeia `credentials` → `frame_tokens`).

- **Token:** `pc_` (pagamento) / `tc_` (digitação) + 32 bytes aleatórios (256 bits, CSPRNG) em base64url. Nunca é o `transactionId`. **Só o SHA-256 é gravado.**
- **Tabela quente `frame_tokens`** (estreita): `hash`, `kind`, `transaction_id`, `created_at`, `expires_at`, `used_at` (uso único: marcada no passo 10), snapshot mínimo do canal (`brands`, `max_installments`).
- **Expurgo pelo Worker:** a cada `PURGE_INTERVAL_SECONDS` (60), em lotes de `PURGE_BATCH_SIZE` (500), apaga tokens expirados ou usados há mais de `PURGE_USED_GRACE_SECONDS` (600; nesse intervalo uma 2ª tentativa ainda recebe 409 10b) e grava em `frame_token_history` só metadados: `transaction_id`, `kind`, datas, `motivo` (`expirado`/`usado`), `purged_at`. Sem token nem hash. Log: `passo=15 resultado=expurgo_token_frame_<n>_para_historico`.

## 6. Gateway real (27/11/2026, tarde)

- **Destino por `.env`:** `GATEWAY_URL` = endpoint completo. Vazio = `gateway-mock` (dev/CI). Na VPS aponta para o Gateway real do Daniel (a URL e as credenciais ficam **só no `.env`** da VPS/box; `.env.example` tem placeholder).
- **Smoke/CI nunca vão ao Gateway real:** os PANs de simulação do mock (4111…, 4000…0002, 4000…0119, 5555…4444, 5105…) sempre vão para `GATEWAY_MOCK_URL`. Só os cartões de teste do Gateway (`POC_GATEWAY_TEST_PANS`, padrão `5453010000066167`, o do modelo) seguem para `GATEWAY_URL`.
- `GATEWAY_REAL_TIMEOUT_MS` (15000) para o real; `GATEWAY_TIMEOUT_MS` (3000) para o mock. A Web App Cartão espera o maior + 5 s.
- `GATEWAY_CONTENT_TYPE` (padrão `application/xml`, enviado com `charset=utf-8`). O header `X-Gateway-Key` (chave do mock) só vai para o mock.
- `GATEWAY_ACQUIRER=22` na VPS, a pedido do Daniel. **Pendente:** no modelo 22 = GETNET, e o merchant de homologação tem Rede e Adyen; confirmar o código com o Gateway.
- `fraud=false` (sem antifraude).
- A API Autorização entrou na rede `saida` (egress) para alcançar o Gateway real. No v7 isso seria a VPN.
- **Resposta:** só o formato do mock (`<transaction-response><status/><code/>`) é interpretado. Resposta de formato desconhecido, erro HTTP ou timeout → **Indeterminada** (nunca afirma aprovação/recusa), com o corpo mascarado no log. `resultado` do passo 11-12: `<Estado>_<mock|real>_http<status>_<raiz do XML|vazio|nao_xml>` ou `<Estado>_codigo_<code>_<destino>`. **Pendente:** reconciliação do indeterminado pelo Worker (consulta ao Gateway) não existe no POC.

**Log mascarado da ida e da volta** (linhas `chk3gw`, categoria `chk3.gateway`, da API Autorização):

```
chk3gw passo=11 servico=autorizacao transactionId=<tx> localRef=<lr> destino=real direcao=ida status=(null) ... xml=<transaction-request>...<merchantId>***</merchantId><merchantKey>***</merchantKey>...<cardHolder>***</cardHolder><cardNumber>545301******6167</cardNumber><cardSecurityCode>***</cardSecurityCode><cardExpirationDate>***</cardExpirationDate>...
chk3gw passo=12 servico=autorizacao transactionId=<tx> localRef=<lr> destino=real direcao=volta status=404 contentType=(null) duracaoMs=2406 xml=
```

Máscara (`src/Autorizacao/GatewayLogMask.cs`): `cardNumber` → BIN6 + `*` + últimos 4; `cardSecurityCode`, `cardExpirationDate`, `cardHolder`, `merchantId`, `merchantKey` e billing pessoal (`customerIdentity`, `name`, `address`, `address2`, `postalCode`, `phone`, `email`) → `***`; qualquer outro valor com 12+ dígitos → BIN6/últimos 4. Vale também para resposta XML/JSON de formato desconhecido (estrutura mantida). O `logs-check.sh` confere isso e procura a chave e o merchantId do `.env` em todos os logs.

**27/11/2026, noite:** `<environment>` adicionado ao `<verification>`, logo depois da `<merchantKey>` (pedido do Daniel; `GATEWAY_ENVIRONMENT=1`, não é mascarado no log). O mock aceita o `<verification>` com ou sem `<environment>` (numérico).

**Resposta do Gateway real (observada em 27/11/2026, host de homologação):** HTTP 200, `Content-Type: application/xml`, com o XML de negócio **serializado como string** pelo ASP.NET Web API:

```xml
<string xmlns="http://schemas.microsoft.com/2003/10/Serialization/">&lt;?xml version="1.0" encoding="utf-8"?&gt;
&lt;transaction-response id="1" versao="1.0"&gt;&lt;transactionId /&gt;
&lt;status&gt;&lt;code&gt;409&lt;/code&gt;&lt;message&gt;ERROR - 409&lt;/message&gt;&lt;/status&gt; ...
</string>
```

- A API Autorização desembrulha o `<string>` (uma vez, com o parser seguro) e lê `transaction-response/status/code`, `status/message` e `transactionId` (referência do Gateway). O log mascarado mostra o XML interno com prefixo `string:`.
- **O significado dos códigos não foi informado.** Aprovado/recusado só pelas listas `GATEWAY_APPROVED_CODES` / `GATEWAY_DECLINED_CODES` do `.env` (vazias por padrão); qualquer outro código fica **Indeterminada**, com o código no `resultado` (`Indeterminada_codigo_409_real`) e gravado como `gatewayCode`.

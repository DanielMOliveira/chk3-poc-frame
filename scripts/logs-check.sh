#!/usr/bin/env bash
# Teste de rastreio e de vazamento nos logs (Frame v7, passos 1-14).
# Roda quatro transações completas a partir do canal (aprovada, recusada, indeterminada e parcelada 3x, todas com
# dados do comprador) e confere nos logs:
#   1) nenhum dado sensível: PAN, CVV, validade, nome, dados do comprador, credenciais pc_/tc_, fragment, JWT, hash,
#      snapshot, XML do Gateway (merchantKey, cardNumber...) ou segredos do .env;
#   2) o transactionId aparece em todos os serviços envolvidos (inclusive o webhook do Worker), com os passos de cada um;
#   3) o gateway-mock recebeu e validou o XML <transaction-request> (parcelas e método certos);
#   4) o X-Correlation-Id é o transactionId do passo 7 em diante.
# Requer: bash, curl, openssl, jq, docker compose e a stack no ar. Ex.: ./scripts/logs-check.sh
# LOGS_SINCE=<UTC ISO> amplia a busca de dados sensíveis, por exemplo para incluir um E2E rodado antes.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib-env.sh
load_env .env

SINCE=$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)
SCAN_SINCE=${LOGS_SINCE:-$SINCE}
SERVICES=(canal opcoes cartao integration autorizacao gateway-mock worker)
# Dados do comprador desta execução: não podem aparecer em nenhum log.
B_NAME="CLIENTE LOGSCHECK"; B_EMAIL="logscheck@exemplo.com"; B_ADDR="Rua Logscheck 123"; B_PHONE="11988887777"; B_DOC="12345678909"
declare -A NPAY=() METHOD=()
FAIL=0
SECRETS=()   # valores capturados durante o teste (nunca impressos)
TXS=()
ok()   { echo "  OK   $*"; }
fail() { echo "  FALHA $*"; FAIL=1; }

run_case() { # run_case <nome> <pan> <status esperado> [parcelas escolhidas] [parcelas máximas]
  local name=$1 pan=$2 expected=$3 n=${4:-1} max=${5:-} order="LOGS${1//[^A-Za-z0-9]/}$(openssl rand -hex 4)"
  echo "== $name (PAN de teste ${pan:0:6}...${pan: -4}, ${n}x)"
  local html tx frame pc tc snapshot status
  # Passos 1-4 pelo canal (o canal gera o correlation id e chama POST /sessions em JSON, com billing)
  html=$(curl -s -X POST "$CANAL_URL/checkout" --data-urlencode "orderId=$order" --data-urlencode "amount=1990" --data-urlencode "installments=$max" \
    --data-urlencode "b_name=$B_NAME" --data-urlencode "b_email=$B_EMAIL" --data-urlencode "b_address=$B_ADDR" --data-urlencode "b_phone=$B_PHONE" \
    --data-urlencode "b_customerIdentity=$B_DOC" --data-urlencode "b_city=Sao Paulo" --data-urlencode "b_state=SP" --data-urlencode "b_postalCode=01310100" \
    --data-urlencode "softDescriptor=LOGSCHECK")
  tx=$(grep -o '<code>[0-9a-f-]\{36\}</code>' <<<"$html" | head -1 | sed 's/<[^>]*>//g')
  frame=$(grep -o 'id="frame" src="[^"]*"' <<<"$html" | sed 's/.*src="//;s/"$//')
  frame=$(sed 's/&#x2B;/+/g;s/&#x3D;/=/g;s/&amp;/\&/g;s/&#x23;/#/g' <<<"$frame")
  pc=${frame#*#pc=}
  [[ -n "$tx" && "$pc" == pc_* ]] || { fail "canal não devolveu transactionId/frame"; return; }
  # Passos 6 e 7 (Tela de opções). O navegador manda X-Correlation-Id = transactionId a partir do passo 7.
  curl -s -o /dev/null -X POST "$OPCOES_URL/api/session" -H "X-Payment-Credential: $pc"
  tc=$(curl -s -X POST "$OPCOES_URL/api/typing-credential" -H "X-Payment-Credential: $pc" -H "X-Correlation-Id: $tx" | jq -r .typingCredential)
  # Passo 8 (página de digitação) e 9-14 (autorização)
  snapshot=$(curl -s -X POST "$CARTAO_URL/digitacao" --data-urlencode "tc=$tc" --data-urlencode "cid=$tx" \
    | grep -o 'name="chk3-snapshot" content="[^"]*"' | sed 's/.*content="//;s/"$//')
  local card='{"pan":"'"$pan"'","holderName":"TESTE POC","expMonth":12,"expYear":'"$(( $(date +%Y) + 2 ))"',"cvv":"123","installments":'"$n"',"snapshot":"'"$snapshot"'","deviceSessionId":"logs-device-1"}'
  status=$(curl -s -X POST "$CARTAO_URL/api/authorize" -H "X-Typing-Credential: $tc" -H "X-Correlation-Id: $tx" \
    -H 'Content-Type: application/json' -d "$card" | jq -r .status)
  [[ "$status" == "$expected" ]] && ok "resultado $status (transactionId $tx)" || fail "resultado esperado $expected, obtido $status"
  SECRETS+=("$pc" "$tc" "$snapshot" "#pc=$pc")
  TXS+=("$tx")
  NPAY[$tx]=$n; METHOD[$tx]=$([[ $n == 1 ]] && echo 1 || echo "${GATEWAY_METHOD_PARCELADO:-2}")
}

run_case aprovada      4111111111111111 Autorizada
run_case recusada      4000000000000002 Recusada
run_case indeterminada 4000000000000119 Indeterminada
run_case parcelada3x   5555555555554444 Autorizada 3 6

# O webhook (passo 16) é assíncrono: espera o Worker registrar a entrega das quatro (até 45 s).
for i in $(seq 1 45); do
  done16=$(docker compose logs --no-color --no-log-prefix --since "$SINCE" worker \
    | jq -R -r 'fromjson? | select(.Category=="chk3" and .State.passo=="16") | .State.transactionId' | sort -u)
  all=1; for tx in "${TXS[@]}"; do grep -qF "$tx" <<<"$done16" || all=0; done
  [[ $all == 1 ]] && break; sleep 1
done
sleep 1

LOGS=$(mktemp); trap 'rm -f "$LOGS"' EXIT
docker compose logs --no-color --no-log-prefix --since "$SCAN_SINCE" "${SERVICES[@]}" >"$LOGS"
echo "== Dados sensíveis nos logs ($(wc -l <"$LOGS") linhas desde $SCAN_SINCE)"
leak=0
for v in "${SECRETS[@]}"; do [[ -n "$v" ]] && grep -qF -- "$v" "$LOGS" && leak=1; done
[[ $leak == 0 ]] && ok "credenciais pc_/tc_, snapshot e fragment desta execução" || fail "credencial, snapshot ou fragment desta execução apareceu no log"
for name in POSTGRES_PASSWORD CHANNEL_JWT_SECRET INTERNAL_API_KEY SNAPSHOT_SIGNING_KEY GATEWAY_API_KEY GATEWAY_MERCHANT_ID GATEWAY_MERCHANT_KEY WEBHOOK_SIGNING_SECRET GRAFANA_ADMIN_PASSWORD; do
  v=${!name:-}; [[ -n "$v" ]] && grep -qF -- "$v" "$LOGS" && fail "segredo $name no log" || ok "sem $name"
done
declare -A PATTERNS=(
  ["PAN de teste"]='4111111111111111|4000000000000002|4000000000000119|5555555555554444|5105105105105100'
  ["campos de cartão (pan/cvv/validade/nome/number)"]='"(pan|cvv|expMonth|expYear|number|name|holderName)"[[:space:]]*:'
  ["credencial pc_/tc_"]='(pc|tc)_[A-Za-z0-9_-]{16,}'
  ["fragment #pc="]='#pc='
  ["JWT ou snapshot (base64url de JSON)"]='eyJ[A-Za-z0-9_-]{10,}'
  ["Bearer"]='Bearer '
  ["hash hex (SHA-256)"]='[0-9A-Fa-f]{64}'
  ["nome digitado no E2E"]='TESTE POC'
  # O XML do Gateway aparece só MASCARADO (linhas chk3gw). Nenhum valor real de CVV, validade, portador ou chave.
  ["XML do Gateway sem máscara (CVV, validade, portador, merchantKey)"]='(cardSecurityCode|cardExpirationDate)(>|\\u003E)[0-9]|(cardHolder|merchantKey)(>|\\u003E)[^*<\\]'
  ["PAN do cartão de teste do Gateway"]='5453010000066167'
  ["dados do comprador (billing)"]="$B_NAME|$B_EMAIL|$B_ADDR|$B_PHONE|$B_DOC"
  ["URL completa do webhook"]='webhook\.site/[0-9a-f-]{8,}'
)
for k in "${!PATTERNS[@]}"; do
  n=$(grep -cE -- "${PATTERNS[$k]}" "$LOGS" || true)
  [[ $n == 0 ]] && ok "sem $k" || fail "$k: $n linha(s) no log"
done

# XML de ida/volta do Gateway (linhas chk3gw): cartão só BIN6+****+últimos 4; demais campos sensíveis = ***.
GWX=$(jq -R -r 'fromjson? | select(.Category=="chk3.gateway") | .State.xml // empty' "$LOGS")
bad=$( { grep -oE '<(cardSecurityCode|cardExpirationDate|cardHolder|merchantId|merchantKey|customerIdentity|name|address|address2|phone|email|postalCode)>[^<]*<' <<<"$GWX" || true; } | { grep -v '>\*\*\*<' || true; } | sed -E 's/>.*/>/' | sort -u | tr '\n' ' ' || true)
[[ -z "$bad" ]] && ok "XML do Gateway no log: CVV, validade, portador, chave e billing pessoal = ***" || fail "XML do Gateway com campo sem máscara: $bad"
badpan=$( { grep -oE '<cardNumber>[^<]*<' <<<"$GWX" || true; } | { grep -vE '<cardNumber>[0-9]{6}\*+[0-9]{4}<' || true; } | wc -l)
[[ $badpan == 0 ]] && ok "XML do Gateway no log: cardNumber = BIN6+****+últimos 4" || fail "cardNumber sem máscara no log ($badpan)"

echo "== transactionId em todos os serviços (linhas chk3)"
declare -A EXPECTED=(
  [canal]="1-4"
  [opcoes]="6 7"
  [cartao]="8 9-14"
  [integration]="2-4 6 7 8 10 13"
  [autorizacao]="9 10 11-12 13"
  [gateway-mock]="11-12"
  [worker]="16"
)
for tx in "${TXS[@]}"; do
  for svc in "${SERVICES[@]}"; do
    got=$(docker compose logs --no-color --no-log-prefix --since "$SINCE" "$svc" \
      | jq -R -r --arg tx "$tx" 'fromjson? | select(.Category=="chk3" and .State.transactionId==$tx) | .State.passo' | sort -u | tr '\n' ' ')
    missing=""
    for p in ${EXPECTED[$svc]}; do [[ " $got " == *" $p "* ]] || missing+="$p "; done
    [[ -z "$missing" ]] && ok "$tx $svc: passos ${got% }" || fail "$tx $svc: faltam passos $missing(achou: ${got:-nenhum})"
  done
  # Do passo 7 em diante, o X-Correlation-Id RECEBIDO (escopo de log) é o transactionId em todos os serviços.
  # Exceção: cartao passo 8 é a navegação do iframe (POST de formulário, sem header); lá o id vem no campo cid.
  bad=$(docker compose logs --no-color --no-log-prefix --since "$SINCE" "${SERVICES[@]}" \
    | jq -R -r --arg tx "$tx" 'fromjson? | select(.Category=="chk3" and .State.transactionId==$tx
        and (.State.passo|IN("7","8","9","9-14","10","11-12","13","16"))
        and ((.State.servico + ":" + .State.passo) != "cartao:8")
        and ([.Scopes[]? | select(.servico) | .correlationId][0] != $tx))
      | .State.servico + ":" + .State.passo' | sort -u | tr '\n' ' ')
  [[ -z "$bad" ]] && ok "$tx X-Correlation-Id recebido = transactionId do passo 7 ao 16" || fail "$tx X-Correlation-Id recebido diferente em: $bad"
  # XML <transaction-request> recebido e validado pelo mock, com parcelas e método esperados.
  gw=$(docker compose logs --no-color --no-log-prefix --since "$SINCE" gateway-mock \
    | jq -R -r --arg tx "$tx" 'fromjson? | select(.Category=="chk3" and .State.transactionId==$tx) | .State.resultado' | head -1)
  [[ "$gw" == "xml_ok_n${NPAY[$tx]}_m${METHOD[$tx]}_"* ]] && ok "$tx gateway-mock validou o XML ($gw)" || fail "$tx gateway-mock: esperado xml_ok_n${NPAY[$tx]}_m${METHOD[$tx]}_*, obtido '${gw:-nada}'"
  # Ida (passo 11) e volta (passo 12) do Gateway logadas mascaradas, com o transactionId.
  gwl=$(jq -R -r --arg tx "$tx" 'fromjson? | select(.Category=="chk3.gateway" and .State.transactionId==$tx) | .State.direcao' "$LOGS" | sort -u | tr '\n' ' ')
  [[ "$gwl" == "ida volta " ]] && ok "$tx XML do Gateway mascarado: ida e volta" || fail "$tx XML do Gateway: esperado ida e volta, achou '${gwl}'"
  wh=$(docker compose logs --no-color --no-log-prefix --since "$SINCE" worker \
    | jq -R -r --arg tx "$tx" 'fromjson? | select(.Category=="chk3" and .State.transactionId==$tx and .State.passo=="16") | "\(.State.resultado) status=\(.State.status)"' | tail -1)
  ok "$tx webhook: ${wh:-nenhum}"
  n=$(grep -cF "$tx" "$LOGS" || true)
  ok "$tx: grep pelo transactionId acha $n linhas"
done

echo
if [[ $FAIL == 0 ]]; then echo "LOGS OK"; else echo "LOGS FALHOU"; exit 1; fi

#!/usr/bin/env bash
# Teste de rastreio e de vazamento nos logs (Frame v7, passos 1-14).
# Roda três transações completas a partir do canal (aprovada, recusada e indeterminada) e confere nos logs:
#   1) nenhum dado sensível: PAN, CVV, validade, nome, credenciais pc_/tc_, fragment, JWT, hash, snapshot ou segredos do .env;
#   2) o transactionId aparece em todos os serviços envolvidos, com os passos esperados de cada um;
#   3) o X-Correlation-Id é o transactionId do passo 7 em diante.
# Requer: bash, curl, openssl, jq, docker compose e a stack no ar. Ex.: ./scripts/logs-check.sh
# LOGS_SINCE=<UTC ISO> amplia a busca de dados sensíveis, por exemplo para incluir um E2E rodado antes.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib-env.sh
load_env .env

SINCE=$(date -u -d '-2 seconds' +%Y-%m-%dT%H:%M:%SZ)
SCAN_SINCE=${LOGS_SINCE:-$SINCE}
SERVICES=(canal opcoes cartao integration autorizacao gateway-mock)
FAIL=0
SECRETS=()   # valores capturados durante o teste (nunca impressos)
TXS=()
ok()   { echo "  OK   $*"; }
fail() { echo "  FALHA $*"; FAIL=1; }

run_case() { # run_case <nome> <pan> <status esperado>
  local name=$1 pan=$2 expected=$3 order="LOGS-$1-$(openssl rand -hex 4)"
  echo "== $name (PAN de teste ${pan:0:6}...${pan: -4})"
  local html tx frame pc tc snapshot status
  # Passos 1-4 pelo canal (o canal gera o correlation id e chama POST /sessions)
  html=$(curl -s -X POST "$CANAL_URL/checkout" --data-urlencode "orderId=$order" --data-urlencode "amountCents=1990" --data-urlencode "installments=")
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
  local card='{"pan":"'"$pan"'","expMonth":12,"expYear":'"$(( $(date +%Y) + 2 ))"',"cvv":"123","installments":1,"snapshot":"'"$snapshot"'","deviceSessionId":"logs-device-1"}'
  status=$(curl -s -X POST "$CARTAO_URL/api/authorize" -H "X-Typing-Credential: $tc" -H "X-Correlation-Id: $tx" \
    -H 'Content-Type: application/json' -d "$card" | jq -r .status)
  [[ "$status" == "$expected" ]] && ok "resultado $status (transactionId $tx)" || fail "resultado esperado $expected, obtido $status"
  SECRETS+=("$pc" "$tc" "$snapshot" "#pc=$pc")
  TXS+=("$tx")
}

run_case aprovada      4111111111111111 Autorizada
run_case recusada      4000000000000002 Recusada
run_case indeterminada 4000000000000119 Indeterminada
sleep 1

LOGS=$(mktemp); trap 'rm -f "$LOGS"' EXIT
docker compose logs --no-color --no-log-prefix --since "$SCAN_SINCE" "${SERVICES[@]}" >"$LOGS"
echo "== Dados sensíveis nos logs ($(wc -l <"$LOGS") linhas desde $SCAN_SINCE)"
leak=0
for v in "${SECRETS[@]}"; do [[ -n "$v" ]] && grep -qF -- "$v" "$LOGS" && leak=1; done
[[ $leak == 0 ]] && ok "credenciais pc_/tc_, snapshot e fragment desta execução" || fail "credencial, snapshot ou fragment desta execução apareceu no log"
for name in POSTGRES_PASSWORD CHANNEL_JWT_SECRET INTERNAL_API_KEY SNAPSHOT_SIGNING_KEY GATEWAY_API_KEY; do
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
)
for k in "${!PATTERNS[@]}"; do
  n=$(grep -cE -- "${PATTERNS[$k]}" "$LOGS" || true)
  [[ $n == 0 ]] && ok "sem $k" || fail "$k: $n linha(s) no log"
done

echo "== transactionId em todos os serviços (linhas chk3)"
declare -A EXPECTED=(
  [canal]="1-4"
  [opcoes]="6 7"
  [cartao]="8 9-14"
  [integration]="2-4 6 7 8 10 13"
  [autorizacao]="9 10 11-12 13"
  [gateway-mock]="11-12"
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
        and (.State.passo|IN("7","8","9","9-14","10","11-12","13"))
        and ((.State.servico + ":" + .State.passo) != "cartao:8")
        and ([.Scopes[]? | select(.servico) | .correlationId][0] != $tx))
      | .State.servico + ":" + .State.passo' | sort -u | tr '\n' ' ')
  [[ -z "$bad" ]] && ok "$tx X-Correlation-Id recebido = transactionId do passo 7 ao 14" || fail "$tx X-Correlation-Id recebido diferente em: $bad"
  n=$(grep -cF "$tx" "$LOGS" || true)
  ok "$tx: grep pelo transactionId acha $n linhas"
done

echo
if [[ $FAIL == 0 ]]; then echo "LOGS OK"; else echo "LOGS FALHOU"; exit 1; fi

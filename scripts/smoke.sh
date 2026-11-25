#!/usr/bin/env bash
# Smoke ponta a ponta do frame v7 via HTTP (sem navegador): passos 1-4, 6, 7, 8, 9-14 e 3b.
# Caminhos: aprova, recusa e timeout (indeterminado). Requer: bash, curl, openssl, jq, stack no ar.
# Lê o .env (portas e origens). Ex.: ./scripts/smoke.sh
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib-env.sh
load_env .env

INTEGRATION=${INTEGRATION_URL}
OPCOES=${OPCOES_PUBLIC_ORIGIN}
CARTAO=${CARTAO_PUBLIC_ORIGIN}
FAIL=0

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
jwt() {
  local now; now=$(date +%s)
  local h p s
  h=$(printf '{"alg":"HS256","typ":"JWT"}' | b64url)
  p=$(printf '{"iss":"%s","aud":"chk3-integration","sub":"%s","scope":"sessions:create","iat":%s,"exp":%s}' "$CHANNEL_JWT_ISSUER" "${1:-canal-fake}" "$now" "$((now+300))" | b64url)
  s=$(printf '%s.%s' "$h" "$p" | openssl dgst -sha256 -hmac "$CHANNEL_JWT_SECRET" -binary | b64url)
  echo "$h.$p.$s"
}
check() { # check <descrição> <esperado> <obtido>
  if [[ "$2" == "$3" ]]; then echo "  OK   $1 ($3)"; else echo "  FALHA $1: esperado '$2', obtido '$3'"; FAIL=1; fi
}

run_case() { # run_case <nome> <pan> <status esperado>
  local name=$1 pan=$2 expected=$3 key="smoke-$(openssl rand -hex 8)"
  local body='{"orderId":"SMOKE-'"$name"'","amountCents":1990,"currency":"BRL","camposAdicionais":{"origem":"smoke"}}'
  echo "== Caso $name (PAN de teste ${pan:0:6}...${pan: -4})"

  # Passos 1-4
  local r code json frame pc
  r=$(curl -s -w '\n%{http_code}' -X POST "$INTEGRATION/sessions" -H "Authorization: Bearer $(jwt)" \
      -H "Idempotency-Key: $key" -H 'Content-Type: application/json' -d "$body")
  code=$(tail -n1 <<<"$r"); json=$(sed '$d' <<<"$r")
  check "POST /sessions cria sessão" 201 "$code"
  frame=$(jq -r .frameUrl <<<"$json")
  [[ "$frame" == *"?"* ]] && { echo "  FALHA frameUrl com query string"; FAIL=1; }
  pc=${frame#*#pc=}
  check "credencial no fragment, não na query" yes "$([[ "$frame" == "$OPCOES/#pc="* ]] && echo yes || echo no)"

  # Passo 6 duas vezes: valida sem consumir
  check "passo 6 valida credencial" 200 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$OPCOES/api/session" -H "X-Payment-Credential: $pc")"
  check "passo 6 reapresentação (sem consumir)" 200 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$OPCOES/api/session" -H "X-Payment-Credential: $pc")"

  # Passo 7
  local tc
  tc=$(curl -s -X POST "$OPCOES/api/typing-credential" -H "X-Payment-Credential: $pc" | jq -r .typingCredential)
  check "passo 7 emite credencial de digitação" yes "$([[ "$tc" == tc_* ]] && echo yes || echo no)"

  # Passo 8: página de digitação por POST, snapshot assinado vem no HTML
  local html snapshot
  html=$(curl -s -X POST "$CARTAO/digitacao" --data-urlencode "tc=$tc" --data-urlencode "installments=1")
  snapshot=$(grep -o 'name="chk3-snapshot" content="[^"]*"' <<<"$html" | sed 's/.*content="//;s/"$//')
  check "passo 8 devolve snapshot assinado" yes "$([[ -n "$snapshot" ]] && echo yes || echo no)"

  # Passos 9-14
  local exp_year=$(( $(date +%Y) + 2 ))
  local card='{"pan":"'"$pan"'","expMonth":12,"expYear":'"$exp_year"',"cvv":"123","installments":1,"snapshot":"'"$snapshot"'","deviceSessionId":"smoke-device-1"}'
  local t0=$(date +%s)
  r=$(curl -s -w '\n%{http_code}' -X POST "$CARTAO/api/authorize" -H "X-Typing-Credential: $tc" -H 'Content-Type: application/json' -d "$card")
  check "passos 9-14 resultado" "$expected" "$(sed '$d' <<<"$r" | jq -r .status)"
  echo "       (levou $(( $(date +%s) - t0 ))s)"

  # Passo 10: tentativa única
  r=$(curl -s -w '\n%{http_code}' -X POST "$CARTAO/api/authorize" -H "X-Typing-Credential: $tc" -H 'Content-Type: application/json' -d "$card")
  check "passo 10b segunda tentativa recusada" "409 TentativaJaRegistrada" "$(tail -n1 <<<"$r") $(sed '$d' <<<"$r" | jq -r .status)"

  # Passo 6a depois da tentativa e 3b com a mesma chave: estado atual, sem credencial nova
  check "passo 6a após tentativa" 401 "$(curl -s -o /dev/null -w '%{http_code}' -X POST "$OPCOES/api/session" -H "X-Payment-Credential: $pc")"
  r=$(curl -s -X POST "$INTEGRATION/sessions" -H "Authorization: Bearer $(jwt)" -H "Idempotency-Key: $key" -H 'Content-Type: application/json' -d "$body")
  check "3b mesma chave devolve estado" "$expected null" "$(jq -r '.state + " " + (.frameUrl|tostring)' <<<"$r")"
}

run_case aprova  4111111111111111 Autorizada
run_case recusa  4000000000000002 Recusada
run_case timeout 4000000000000119 Indeterminada

echo
if [[ $FAIL == 0 ]]; then echo "SMOKE OK"; else echo "SMOKE FALHOU"; exit 1; fi

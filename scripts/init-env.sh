#!/usr/bin/env bash
# Gera o .env a partir do .env.example com segredos aleatórios.
# Qualquer variável já definida no ambiente sobrescreve o modelo, por exemplo:
#   CANAL_HOST_PORT=19180 OPCOES_HOST_PORT=19181 ./scripts/init-env.sh
# Uso: ./scripts/init-env.sh [--force]   (--force recria um .env existente)
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ -f .env && "${1:-}" != "--force" ]]; then
  echo ".env já existe. Use --force para recriar (os segredos atuais serão trocados)." >&2
  exit 1
fi

gen() {
  if command -v openssl >/dev/null 2>&1; then openssl rand -base64 48
  else head -c 48 /dev/urandom | base64
  fi | tr -d '\n/+='
}

tmp=$(mktemp)
re='^(# )?([A-Z_][A-Z0-9_]*)=(.*)$'
while IFS= read -r line || [[ -n "$line" ]]; do
  line=${line%$'\r'}
  if [[ "$line" =~ $re ]]; then
    commented=${BASH_REMATCH[1]}; key=${BASH_REMATCH[2]}; value=${BASH_REMATCH[3]}
    if [[ -n "${!key:-}" ]]; then line="$key=${!key}"           # definido no ambiente
    elif [[ -z "$commented" && "$value" == "__GERAR__" ]]; then line="$key=$(gen)"
    fi
  fi
  printf '%s\n' "$line" >> "$tmp"
done < .env.example

mv "$tmp" .env
chmod 600 .env
echo ".env criado com segredos novos."

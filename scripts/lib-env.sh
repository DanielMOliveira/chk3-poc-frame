# shellcheck shell=bash
# Carrega o .env (sem sobrescrever variáveis já definidas no ambiente, como faz o compose)
# e deriva as origens/URLs com as mesmas regras do docker-compose.yml.
load_env() {
  local file=${1:-.env} line key value
  [[ -f "$file" ]] || { echo "Arquivo $file não encontrado. Rode ./scripts/init-env.sh" >&2; return 1; }
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    [[ "$line" =~ ^[[:space:]]*# || "$line" != *=* ]] && continue
    key=${line%%=*}; value=${line#*=}
    [[ "$key" =~ ^[A-Z_][A-Z0-9_]*$ ]] || continue
    [[ -z "${!key:-}" ]] && export "$key=$value"
  done < "$file"
  local host=${HOST_BIND:-127.0.0.1}
  [[ "$host" == "0.0.0.0" ]] && host=127.0.0.1
  export CANAL_PUBLIC_ORIGIN=${CANAL_PUBLIC_ORIGIN:-http://localhost:${CANAL_HOST_PORT:-8080}}
  export OPCOES_PUBLIC_ORIGIN=${OPCOES_PUBLIC_ORIGIN:-http://localhost:${OPCOES_HOST_PORT:-8081}}
  export CARTAO_PUBLIC_ORIGIN=${CARTAO_PUBLIC_ORIGIN:-http://localhost:${CARTAO_HOST_PORT:-8082}}
  # A Integration não tem origem pública no navegador: o smoke a chama direto na porta do host.
  export INTEGRATION_URL=${INTEGRATION_URL:-http://$host:${INTEGRATION_HOST_PORT:-8083}}
}

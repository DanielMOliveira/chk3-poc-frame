#!/usr/bin/env bash
# Deploy via git, executado NA MÁQUINA ALVO, dentro do clone do repo:
#   ./scripts/deploy.sh [ref]        # ref: commit, tag ou branch remota (padrão: origin/main)
# Passos: git fetch -> checkout (detached) do ref -> docker compose up -d --build -> espera a
# Integration -> ./scripts/smoke.sh -> ./scripts/logs-check.sh. Qualquer falha interrompe com código != 0 e mostra o porquê.
#
# Credencial do git: o repo é público, então o fetch HTTPS não precisa de token. Se ficar privado,
# passe o token SÓ no ambiente desta execução (DEPLOY_GIT_TOKEN); ele não é gravado no git config
# nem na URL do remote. Ex.: scripts/deploy-remote.sh lê o token do ambiente local e envia por stdin.
set -euo pipefail
cd "${APP_DIR:-$(dirname "$0")/..}"   # APP_DIR: usado pelo deploy-remote.sh

REF=${1:-origin/main}
die() { echo "DEPLOY FALHOU: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

[[ -f .env ]] || die ".env não encontrado. Gere com ./scripts/init-env.sh e ajuste portas/origens."
if [[ -f docker-compose.override.yml && "${ALLOW_OVERRIDE:-0}" != 1 ]]; then
  die "docker-compose.override.yml presente (sobrepõe portas). Configure tudo no .env e mova o arquivo, ou use ALLOW_OVERRIDE=1."
fi
git diff --quiet && git diff --cached --quiet || die "há alterações locais em arquivos versionados. Faça stash ou descarte antes do deploy."

step "git fetch ($(git remote get-url origin))"
if [[ -n "${DEPLOY_GIT_TOKEN:-}" ]]; then
  git -c credential.helper= -c credential.helper='!f() { echo username=x-access-token; echo "password=$DEPLOY_GIT_TOKEN"; }; f' \
    fetch --prune --tags origin
else
  GIT_TERMINAL_PROMPT=0 git fetch --prune --tags origin
fi

SHA=$(git rev-parse --verify --quiet "$REF^{commit}") || die "ref '$REF' não encontrado após o fetch."
step "checkout $REF ($SHA)"
git -c advice.detachedHead=false checkout --detach "$SHA"
git log --oneline -1

step "docker compose up -d --build"
docker compose up -d --build --remove-orphans || die "docker compose up falhou."
docker compose ps --format '{{.Service}}: {{.Status}} {{.Ports}}'

source scripts/lib-env.sh
load_env .env
step "aguardando a Integration em $INTEGRATION_URL/health"
for i in $(seq 1 60); do
  curl -fsS -o /dev/null "$INTEGRATION_URL/health" 2>/dev/null && break
  [[ $i == 60 ]] && { docker compose logs --tail 40 integration >&2; die "Integration não respondeu em 60 s."; }
  sleep 1
done

step "smoke"
if ! ./scripts/smoke.sh; then
  docker compose ps >&2
  docker compose logs --tail 30 >&2
  die "smoke falhou no commit $SHA."
fi

step "rastreio nos logs (transactionId em todos os serviços, sem dado sensível)"
if ! ./scripts/logs-check.sh; then
  die "logs-check falhou no commit $SHA."
fi
echo
echo "DEPLOY OK: $SHA"

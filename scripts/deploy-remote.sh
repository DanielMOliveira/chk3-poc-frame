#!/usr/bin/env bash
# Dispara o deploy numa máquina remota por SSH (o deploy em si é o scripts/deploy.sh do alvo).
#   scripts/deploy-remote.sh <ssh-host> <dir-do-app-no-alvo> [ref]
# Ex.: scripts/deploy-remote.sh vps /home/grokbot/apps/chk3-poc-frame origin/main
# Se DEPLOY_GIT_TOKEN estiver definido localmente (repo privado), ele vai por stdin para o
# ambiente do deploy remoto: não aparece na linha de comando, no histórico nem no git config.
set -euo pipefail
HOST=${1:?ssh-host}; DIR=${2:?diretório do app no alvo}; REF=${3:-origin/main}
q() { printf '%q' "$1"; }
# Atualiza o próprio deploy.sh antes (fetch + usa a versão do ref), depois roda.
printf '%s\n' "${DEPLOY_GIT_TOKEN:-}" | ssh "$HOST" "set -e; cd $(q "$DIR"); IFS= read -r DEPLOY_GIT_TOKEN || true; export DEPLOY_GIT_TOKEN; \
  if [ -n \"\$DEPLOY_GIT_TOKEN\" ]; then git -c credential.helper= -c credential.helper='!f() { echo username=x-access-token; echo \"password=\$DEPLOY_GIT_TOKEN\"; }; f' fetch --quiet origin; else GIT_TERMINAL_PROMPT=0 git fetch --quiet origin; fi; \
  git show $(q "$REF"):scripts/deploy.sh > /tmp/chk3-deploy.\$\$.sh && trap 'rm -f /tmp/chk3-deploy.\$\$.sh' EXIT; \
  APP_DIR=\"\$PWD\" bash /tmp/chk3-deploy.\$\$.sh $(q "$REF")"

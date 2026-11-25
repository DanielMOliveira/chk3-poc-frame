#!/usr/bin/env bash
# Roda os testes (unitários + integração contra a stack do compose) no container oficial do SDK .NET 10.
# Não precisa do SDK instalado no host. A stack precisa estar no ar (docker compose up -d).
set -euo pipefail
cd "$(dirname "$0")/.."
docker run --rm --network host --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/Chk3.Tests "$@"

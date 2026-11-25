#!/usr/bin/env bash
# Roda os testes (unitários + integração) num container do SDK .NET 10 dentro da rede do compose.
# Só precisa de Docker (sem SDK .NET, sem --network host). A stack precisa estar no ar:
#   docker compose up -d --build && ./scripts/test.sh
set -euo pipefail
cd "$(dirname "$0")/.."
exec docker compose --profile test run --rm tests

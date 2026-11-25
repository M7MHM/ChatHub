#!/usr/bin/env bash
# docker compose for the production stack, with .env.prod and the deployed image tag.
# Examples: deploy/compose.sh ps    deploy/compose.sh logs -f --tail 100 api    deploy/compose.sh exec postgres psql -U chathub
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
compose "$@"

#!/usr/bin/env bash
# Redeploys an earlier image tag without running migrations (they are expand-only, so the old code still works).
# Usage: deploy/rollback.sh [tag]     (default: the tag deployed before the current one)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

tag=${1:-$(cat "$STATE_DIR/previous_tag" 2>/dev/null || true)}
[[ -n $tag ]] || die "no previous tag recorded in $STATE_DIR; pass the tag to roll back to"
log "rolling back from $(cat "$STATE_DIR/current_tag" 2>/dev/null || echo unknown) to $tag; database migrations stay as they are"
exec bash "$DEPLOY_DIR/deploy.sh" --skip-migrations "$tag"

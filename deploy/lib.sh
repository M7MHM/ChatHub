#!/usr/bin/env bash
# Shared by the deploy scripts; not meant to be run directly.

export MSYS_NO_PATHCONV=1

pwd_native() { if [[ ${OSTYPE-} == msys* || ${OSTYPE-} == cygwin* ]]; then pwd -W; else pwd; fi; }
DEPLOY_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd_native)
CHATHUB_DIR=${CHATHUB_DIR:-$(cd "$DEPLOY_DIR/.." && pwd_native)}
ENV_FILE=${ENV_FILE:-$CHATHUB_DIR/.env.prod}
COMPOSE_FILE=${COMPOSE_FILE:-$CHATHUB_DIR/docker-compose.prod.yml}
STATE_DIR=${STATE_DIR:-$DEPLOY_DIR/.state}
EDGE_DIR=${EDGE_DIR:-$DEPLOY_DIR/edge}

log() { printf '%s %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; }
die() {
    log "ERROR: $*"
    exit 1
}

[[ -f $ENV_FILE ]] || die "$ENV_FILE not found; copy .env.prod.example to it and fill it in"

env_value() {
    local name=$1 default=${2-} line
    if [[ -n ${!name-} ]]; then
        printf '%s' "${!name}"
        return
    fi
    line=$(grep -E "^[[:space:]]*${name}=" "$ENV_FILE" | tail -n 1 || true)
    if [[ -z $line ]]; then
        printf '%s' "$default"
        return
    fi
    line=${line#*=}
    line=${line%$'\r'}
    line=${line#\"}
    line=${line%\"}
    printf '%s' "${line:-$default}"
}

compose() {
    local args=(--project-directory "$CHATHUB_DIR" --env-file "$ENV_FILE") file
    [[ -f $STATE_DIR/tag.env ]] && args+=(--env-file "$STATE_DIR/tag.env")
    args+=(-f "$COMPOSE_FILE")
    for file in ${COMPOSE_OVERRIDES-}; do args+=(-f "$file"); done
    docker compose "${args[@]}" "$@"
}

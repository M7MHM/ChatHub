#!/usr/bin/env bash
# Zero-downtime deploy of one image tag: pull, migrate, replace the API replicas one at a time, replace the worker.
# Usage: deploy/deploy.sh [--skip-migrations] [--no-prune] <tag>      (docs/DEPLOYMENT.md#deploying)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
    echo "usage: deploy/deploy.sh [--skip-migrations] [--no-prune] <tag>" >&2
    exit 2
}

skip_migrations=0
prune=1
tag=""
while (($#)); do
    case $1 in
        --skip-migrations) skip_migrations=1 ;;
        --no-prune) prune=0 ;;
        -*) usage ;;
        *) [[ -z $tag ]] || usage; tag=$1 ;;
    esac
    shift
done
[[ -n $tag ]] || usage
[[ $tag =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]] || die "invalid tag '$tag'"

mkdir -p "$STATE_DIR" "$EDGE_DIR/upstreams"
exec 9>"$STATE_DIR/deploy.lock"
if command -v flock >/dev/null 2>&1; then
    flock -n 9 || die "another deploy is running"
fi

export CHATHUB_TAG=$tag
prefix=$(env_value CHATHUB_IMAGE_PREFIX ghcr.io/m7mhm)
replicas=$(env_value API_REPLICAS 1)
drain_seconds=$(env_value DRAIN_SECONDS 5)
health_timeout=$(env_value HEALTH_TIMEOUT 180)
edge_container=$(env_value EDGE_CONTAINER edge-caddy-1)
api_image="$prefix/chathub-api:$tag"
started=$SECONDS

[[ $(docker inspect -f '{{.State.Running}}' "$edge_container" 2>/dev/null) == true ]] ||
    die "the edge proxy '$edge_container' is not running; start deploy/edge first"

health() { docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$1" 2>/dev/null || echo gone; }
name_of() { docker inspect -f '{{.Name}}' "$1" | sed 's#^/##'; }
api_containers() { compose ps -q api | sort; }

wait_healthy() {
    local id=$1 deadline=$((SECONDS + health_timeout))
    while ((SECONDS < deadline)); do
        case $(health "$id") in
            healthy) return 0 ;;
            unhealthy | gone) return 1 ;;
        esac
        sleep 2
    done
    return 1
}

sync_upstreams() {
    local exclude=" $* " targets=() id
    for id in $(api_containers); do
        [[ $exclude == *" $id "* ]] && continue
        [[ $(health "$id") == healthy ]] || continue
        targets+=("$(name_of "$id"):8080")
    done
    ((${#targets[@]})) || die "no healthy API container left to route traffic to"
    printf 'to %s\n' "${targets[*]}" >"$EDGE_DIR/upstreams/.chathub.caddy.tmp"
    mv -f "$EDGE_DIR/upstreams/.chathub.caddy.tmp" "$EDGE_DIR/upstreams/chathub.caddy"
    docker exec "$edge_container" caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null 2>&1 ||
        die "Caddy rejected the new upstream list; see: docker logs $edge_container"
    log "edge routes to ${targets[*]}"
}

fresh=()

start_one() {
    local before after new count
    before=$(api_containers)
    count=$(grep -c . <<<"$before" || true)
    compose up -d --no-deps --no-recreate --scale "api=$((count + 1))" api >/dev/null 2>&1
    after=$(api_containers)
    new=$(comm -13 <(printf '%s\n' "$before") <(printf '%s\n' "$after") | grep . | head -n 1 || true)
    [[ -n $new ]] || die "docker compose did not start a new API container"
    up_to_date "$new" || die "$(name_of "$new") does not run $api_image with the current configuration"
    log "started $(name_of "$new"), waiting until it is healthy"
    if ! wait_healthy "$new"; then
        log "$(name_of "$new") did not become healthy; its last log lines:"
        docker logs --tail 40 "$new" >&2 || true
        docker rm -f "$new" >/dev/null
        die "deploy aborted; the containers that were serving before keep serving"
    fi
    fresh+=("$new")
    sync_upstreams
}

retire() {
    local id=$1 name
    name=$(name_of "$id")
    sync_upstreams "$id"
    log "draining $name for ${drain_seconds}s, then stopping it"
    sleep "$drain_seconds"
    docker stop -t 30 "$id" >/dev/null
    docker rm "$id" >/dev/null
    log "removed $name"
}

log "deploying $tag: $api_image, $replicas API replica(s)"

log "pulling images"
compose pull --quiet migrator notifications-migrator api notifications
target_image=$(docker image inspect -f '{{.Id}}' "$api_image")
fingerprint=$(CHATHUB_CONFIG_FINGERPRINT='' compose config api | grep -vE '^[[:space:]]+(replicas|chathub\.config-fingerprint):' | sha256sum | cut -c1-16)
export CHATHUB_CONFIG_FINGERPRINT=$fingerprint
up_to_date() {
    [[ $(docker inspect -f '{{.Image}}' "$1") == "$target_image" &&
        $(docker inspect -f '{{index .Config.Labels "chathub.config-fingerprint"}}' "$1") == "$fingerprint" ]]
}

log "starting PostgreSQL, Redis and RabbitMQ (no-op when they are running)"
compose up -d --wait postgres redis rabbitmq >/dev/null 2>&1 || die "infrastructure did not become healthy: deploy/compose.sh ps"

if ((skip_migrations)); then
    log "skipping migrations"
else
    log "migrating the chathub database"
    compose run --rm --no-deps migrator
    log "migrating the chathub_notifications database"
    compose run --rm --no-deps notifications-migrator
fi

stopped=$(comm -23 <(compose ps -a -q api | sort) <(api_containers) | grep . || true)
if [[ -n $stopped ]]; then
    log "removing stopped API containers"
    xargs docker rm -f <<<"$stopped" >/dev/null
fi

stale=()
for id in $(api_containers); do
    if up_to_date "$id"; then fresh+=("$id"); else stale+=("$id"); fi
done
log "API: ${#fresh[@]} container(s) already on $tag with this configuration, ${#stale[@]} to replace"

for id in "${stale[@]}"; do
    start_one
    retire "$id"
done
while ((${#fresh[@]} < replicas)); do start_one; done
while ((${#fresh[@]} > replicas)); do
    retire "${fresh[-1]}"
    unset 'fresh[-1]'
done
sync_upstreams

log "updating the notification worker"
compose up -d --no-deps notifications >/dev/null 2>&1
wait_healthy "$(compose ps -q notifications)" || die "the notification worker is not healthy: deploy/compose.sh logs notifications"

current=$(cat "$STATE_DIR/current_tag" 2>/dev/null || true)
if [[ $current != "$tag" ]]; then
    [[ -n $current ]] && printf '%s\n' "$current" >"$STATE_DIR/previous_tag"
    printf '%s\n' "$tag" >"$STATE_DIR/current_tag"
fi
printf 'CHATHUB_TAG=%s\nCHATHUB_CONFIG_FINGERPRINT=%s\n' "$tag" "$fingerprint" >"$STATE_DIR/tag.env"
printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$tag" >>"$STATE_DIR/history"

if ((prune)); then
    keep=" $tag $(cat "$STATE_DIR/previous_tag" 2>/dev/null || true) "
    while read -r ref; do
        [[ $ref == "$prefix"/chathub-* ]] || continue
        [[ $keep == *" ${ref##*:} "* ]] && continue
        docker image rm "$ref" >/dev/null 2>&1 && log "removed image $ref" || true
    done < <(docker image ls --format '{{.Repository}}:{{.Tag}}')
    docker image prune -f >/dev/null
fi

log "deployed $tag in $((SECONDS - started))s"

#!/usr/bin/env bash
# Dumps both databases and the uploaded files, keeps 7 daily and 4 weekly sets.
# Run daily by deploy/systemd/chathub-backup.timer (docs/DEPLOYMENT.md#backups).
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

backup_dir=$(env_value BACKUP_DIR /var/backups/chathub)
keep_daily=$(env_value BACKUP_KEEP_DAILY 7)
keep_weekly=$(env_value BACKUP_KEEP_WEEKLY 4)
user=$(env_value POSTGRES_USER chathub)
main_db=$(env_value POSTGRES_DB chathub)
notifications_db=$(env_value NOTIFICATIONS_DB chathub_notifications)
stamp=$(date -u +%Y%m%dT%H%M%SZ)

umask 077
mkdir -p "$backup_dir/daily" "$backup_dir/weekly"
daily=$backup_dir/daily
made=()

dump() {
    local db=$1 file="$daily/$1-$stamp.dump"
    compose exec -T postgres pg_dump -U "$user" -d "$db" --format=custom --compress=6 >"$file.partial"
    compose exec -T postgres pg_restore --list <"$file.partial" >/dev/null || die "the dump of $db cannot be read back"
    mv "$file.partial" "$file"
    made+=("$file")
    log "dumped $db to $file ($(du -h "$file" | cut -f1))"
}

dump "$main_db"
dump "$notifications_db"

api=$(compose ps -q api | head -n 1)
if [[ -n $api ]]; then
    docker exec "$api" tar -C /app/storage -czf - . >"$daily/storage-$stamp.tar.gz.partial"
    mv "$daily/storage-$stamp.tar.gz.partial" "$daily/storage-$stamp.tar.gz"
    made+=("$daily/storage-$stamp.tar.gz")
    log "archived uploaded files ($(du -h "$daily/storage-$stamp.tar.gz" | cut -f1))"
else
    log "no API container is running; uploaded files were not archived"
fi

if [[ $(date -u +%u) == 7 || -z $(ls -A "$backup_dir/weekly") ]]; then
    cp "${made[@]}" "$backup_dir/weekly/"
    log "kept this set as the weekly backup"
fi

rotate() {
    local dir=$1 keep=$2 kind
    for kind in "$main_db" "$notifications_db" storage; do
        find "$dir" -maxdepth 1 -type f -name "$kind-[0-9]*" | sort -r | tail -n +"$((keep + 1))" | while read -r old; do
            rm -f "$old"
            log "rotated out $old"
        done
    done
}
rotate "$backup_dir/daily" "$keep_daily"
rotate "$backup_dir/weekly" "$keep_weekly"
find "$backup_dir" -name '*.partial' -mmin +60 -delete

log "backup $stamp done"

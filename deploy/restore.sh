#!/usr/bin/env bash
# Restores a dump made by deploy/backup.sh.
#   deploy/restore.sh <file.dump>                 into a scratch database <db>_restore (the live data is untouched)
#   deploy/restore.sh <file.dump> --verify        same, then compares row counts with the live database and drops it
#   deploy/restore.sh <file.dump> --into <db> --replace
#                                                 into a live database, after stopping api and notifications
set -euo pipefail
export LC_ALL=C
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

usage() {
    sed -n '2,6p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
    exit 2
}

file=""
target=""
replace=0
verify=0
while (($#)); do
    case $1 in
        --into) target=${2:-}; shift ;;
        --replace) replace=1 ;;
        --verify) verify=1 ;;
        -*) usage ;;
        *) [[ -z $file ]] || usage; file=$1 ;;
    esac
    shift
done
[[ -f $file ]] || usage

user=$(env_value POSTGRES_USER chathub)
main_db=$(env_value POSTGRES_DB chathub)
notifications_db=$(env_value NOTIFICATIONS_DB chathub_notifications)
base=$(basename "$file")
case $base in
    "$notifications_db"-*) source_db=$notifications_db ;;
    "$main_db"-*) source_db=$main_db ;;
    *) die "cannot tell which database $base belongs to (expected $main_db-<time>.dump or $notifications_db-<time>.dump)" ;;
esac
target=${target:-${source_db}_restore}
[[ $target =~ ^[a-z_][a-z0-9_]*$ ]] || die "invalid database name '$target'"

psql_in() { compose exec -T postgres psql -U "$user" -d "$1" -v ON_ERROR_STOP=1 -At "${@:2}"; }

if [[ $target == "$main_db" || $target == "$notifications_db" ]]; then
    ((replace)) || die "$target is a live database; add --replace to overwrite it"
    [[ -z $(compose ps -q api notifications) ]] ||
        die "stop the API and the worker first: deploy/compose.sh stop api notifications"
fi

log "restoring $base into $target"
compose exec -T postgres dropdb -U "$user" --if-exists --force "$target"
compose exec -T postgres createdb -U "$user" "$target"
compose exec -T postgres pg_restore -U "$user" -d "$target" --no-owner --exit-on-error <"$file"
log "restored $base into $target"

counts() {
    local db=$1 table
    for table in $(psql_in "$db" -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY 1"); do
        printf '%s %s\n' "$table" "$(psql_in "$db" -c "SELECT count(*) FROM public.\"$table\"")"
    done
}

restored=$(counts "$target")
if ((verify)); then
    live=$(counts "$source_db")
    printf '%-32s %10s %10s\n' table restored live
    join <(sort <<<"$restored") <(sort <<<"$live") | while read -r table restored_rows live_rows; do
        printf '%-32s %10s %10s%s\n' "$table" "$restored_rows" "$live_rows" "$([[ $restored_rows == "$live_rows" ]] || echo '  (changed since the dump)')"
    done
    compose exec -T postgres dropdb -U "$user" --force "$target"
    log "restore test passed; dropped $target"
else
    printf '%-32s %10s\n' table rows
    while read -r table rows; do printf '%-32s %10s\n' "$table" "$rows"; done <<<"$restored"
fi

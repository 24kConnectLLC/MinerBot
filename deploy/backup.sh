#!/usr/bin/env bash
# Dumps MinerBot's own database into /srv/backups/minerbot as minerbot-<UTC>-<reason>.sql.gz, which only root can open,
# and keeps 14 days of dumps, the newest 3 always. cron runs it at night from /etc/cron.d/minerbot, and deploy.sh runs
# it before any migration. Session rows are left out, so a restore signs everyone out. How to restore: README.txt.
# It prints nothing when it works. When it fails it prints one line, which cron mails to root, and backup.log says
# which step failed. A run stopped at the cron line's limit is mailed by the line's echo; the server's bash 5.1 also runs
# the EXIT trap then, so backup.log says failed. A run killed outright, by a restart say, runs no trap and leaves its
# "started" line with nothing after it, which the nightly check reports once it's 2 hours old. The check runs at 05:47,
# after the nightly run's limit, so a nightly run cut off is reported that same morning.
# Usage: ./backup.sh nightly|pre-deploy|manual
#   nightly and manual wait up to 15 minutes for the deploy lock, so a dump never runs during a migration;
#   pre-deploy comes only from deploy.sh, which holds that lock already.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

reason=${1:-}
if [ $# -ne 1 ] || ! [[ $reason =~ ^(nightly|pre-deploy|manual)$ ]]; then
    echo "Usage: ./backup.sh nightly|pre-deploy|manual" >&2
    exit 1
fi

step="starting"
failed_step=
finished=no
partial=
errors=
problem=

# One line for cron's mail, from the EXIT trap below, so every failure gives exactly one
die() {
    problem=$*
    exit 1
}

log_line() {   # log_line <words>: one line in backup.log, after the time
    printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >> "$backups/backup.log"
}

on_exit() {
    if [ "$finished" = yes ]; then
        return 0
    fi
    if [ -n "$partial" ]; then
        rm -f "$partial"
    fi
    if [ -d "$backups" ]; then
        if [ -n "$errors" ] && [ -s "$errors" ]; then
            sed 's/^/    /' "$errors" >> "$backups/backup.log"
        fi
        log_line "failed ${failed_step:-$step}"
    fi
    if [ -n "$errors" ]; then
        rm -f "$errors"
    fi
    echo "MinerBot's $reason backup failed while ${failed_step:-$step}${problem:+: $problem}. The older dumps are all kept. See $backups/backup.log." >&2
}

# The ERR trap only records the step; the EXIT trap reports it
trap 'failed_step=$step' ERR
trap 'status=$?; on_exit || true; if [ "$finished" != yes ]; then status=1; fi; exit "$status"' EXIT

step="loading server.conf"
load_server_conf

step="waiting for the locks"
if [ "$reason" != pre-deploy ]; then
    take_lock "backup $reason" 900 "A deploy held the lock for 15 minutes, so this $reason dump didn't run. A deploy dumps before any migration itself."
fi
install -d -m 700 "$home/state"
exec 8>> "$home/state/backup.lock"
flock -w 300 8 || die "another backup held its lock for 5 minutes"

step="preparing $backups"
mkdir -p "$backups"
chmod 700 /srv/backups "$backups"
log_line "started $reason"
avail=$(free_kib "$backups") || die "couldn't tell how much space is free there"
[ "$avail" -ge 1048576 ] || die "less than 1 GiB is free there"

step="dumping"
file=$backups/minerbot-$(date -u +%Y%m%dT%H%M%SZ)-$reason.sql.gz
partial=$file.partial
limit=900
if [ "$reason" = pre-deploy ]; then
    limit=300
fi
install -d -m 700 "$home/state" "$home/state/tmp"
errors=$(mktemp "$home/state/tmp/backup-errors.XXXXXX")
# The inner timeout puts the dump in a process group of its own, so its limit ends the dump and this script lives on to
# report it. A consistent InnoDB snapshot without locks; BINARY columns as hex; utf8mb4 for emoji; no --databases, so a
# restore can go into a scratch database. No triggers, routines or events, which MinerBot's database never has and
# apply_rights refuses: a restore would replay their text unchecked, with the rights of whoever restores it. Each is
# left out here, on the command line, which wins over a routines or events line in root's option files; so are the
# rows, each table's CREATE and its DROP before it, which a no-data line there could otherwise drop without a sign.
if ! run_for "$limit" mariadb-dump --single-transaction --quick --hex-blob --skip-dump-date \
    --default-character-set=utf8mb4 --skip-triggers --skip-routines --skip-events \
    --skip-no-data --skip-no-create-info --add-drop-table \
    --ignore-table-data="$BOT_DB.dashboard_sessions" "$BOT_DB" 2> "$errors" | gzip -6 > "$partial"; then
    die "the dump failed or took more than $limit seconds"
fi

step="checking the dump"
# gzip -t, then the dump's last line, under one limit of 5 minutes. With the deploy lock's 15, the backup lock's 5 and
# the dump's 15, this script's limits add up to 2,400 seconds, well inside the cron line's 3,600.
# shellcheck disable=SC2016 # the inner bash expands $1
last=$(run_for 300 bash -c 'set -o pipefail; gzip -t "$1" && gzip -dc "$1" | tail -n 1' check-dump "$partial" 2>> "$errors") \
    || die "gzip says the file is broken, or checking it took more than 300 seconds"
[ "$last" = "-- Dump completed" ] || die "it doesn't end with -- Dump completed, so it's incomplete"

step="keeping the dump"
chmod 600 "$partial"
mv -f "$partial" "$file"
partial=
rm -f "$errors"
errors=

# Only after a dump that worked, so a failure never takes away the last good ones
step="pruning old dumps"
kept=0
while IFS= read -r -t 5 line; do
    kept=$((kept + 1))
    old=${line#* }
    if [ "$kept" -gt 3 ] && [ -n "$(find "$old" -maxdepth 0 -mtime +13)" ]; then
        rm -f -- "$old"
    fi
done < <(find "$backups" -maxdepth 1 -type f -name 'minerbot-*.sql.gz' -printf '%T@ %p\n' | sort -rn)
find "$backups" -maxdepth 1 -type f -name '*.partial' -mtime +0 -delete

size=$(du -h "$file" | cut -f1)
log_line "ok ${file##*/} $size"
finished=yes

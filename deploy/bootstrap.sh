#!/usr/bin/env bash
# The one-time move of /opt/minerbot from the files the server had before these deploy files to the deploy files of a
# commit on main, without stopping the bot. It runs once, with the owner's go-ahead, from the commit's own files,
# as deploy/README.md shows:
#   bash <staging folder>/deploy/bootstrap.sh <commit> <staging folder>
# It never touches containers, images, env files or nginx. It keeps today's files in old/ once, writes server.conf from
# bot.env, reserves UID and GID 1654, asks Compose's own dry run whether today's compose.yml or the new one would
# recreate the bot, and installs the deploy files. Every step is skipped when it's done already, so a run cut off
# anywhere is finished by running the same commands again. The changes run as the systemd unit minerbot-bootstrap, for
# 15 minutes at most, and this terminal follows its log.
# Exit code: 0 done; 1 refused or stopped, with the reason.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

action=bootstrap
step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: bash <staging folder>/deploy/bootstrap.sh <commit> <staging folder>"
# Today's files, kept once in old/ before anything new is installed
old_files=(compose.yml deploy.sh rollback.sh fill-secrets.sh setup-login.sh move-docker-storage.sh README.txt)
# The commands the scripts use, checked before the move. cmp and diff come from diffutils, which a minimal AlmaLinux
# lacks, and pgrep and ps from procps-ng: without pgrep, setup couldn't prove that nginx took a reload, and without ps,
# the nightly check couldn't see that nginx never loaded proxy-key.conf.
needed=(awk cmp curl cut date df diff docker du find flock getent git grep groupadd gzip install jq mariadb mariadb-dump
    mktemp nginx openssl pgrep ps readlink sed sort stat systemctl systemd-run systemd-tmpfiles tail tar timeout tr uapi
    useradd)

apply=no
if [ "${1:-}" = --apply ]; then
    apply=yes
    shift
fi
[ $# -eq 2 ] || die "$usage"
commit=$1
stage=$2
[[ $commit =~ ^[0-9a-f]{40}$ ]] || die "The commit must be given in full: 40 characters, 0-9 and a-f. $usage"
case $stage in
    "$home"/state/tmp/bootstrap.*) ;;
    *) die "The staging folder must be one made in $home/state/tmp, named bootstrap.XXXXXX. $usage" ;;
esac
[ -f "$stage/deploy/bootstrap.sh" ] || die "$stage/deploy/bootstrap.sh is missing, so that isn't the commit's deploy folder. $usage"
rev=${commit:0:12}

# ---- The foreground part: it changes nothing but the self-test's unit

# shellcheck disable=SC2317,SC2329 # the EXIT trap runs it
foreground_cleanup() {
    # From the moment the unit starts, the staging folder is the unit's, and it removes the folder itself
    if [ "${detached_unit_started:-no}" != yes ]; then
        rm -rf "$stage"
    fi
}

self_test() {   # systemd must end a unit at its time limit here, or the detached runs can't be trusted
    local started=$SECONDS state=0 silent=no
    step="testing systemd's time limit"
    echo "Testing that systemd ends a unit at its time limit here."
    run_for 20 systemd-run --quiet --collect --unit=minerbot-selftest -p RuntimeMaxSec=2 -p TimeoutStopSec=2 sleep 60 \
        || die "systemd-run couldn't start the self-test unit (above), so the detached runs can't be trusted. Nothing was changed."
    # Only systemd's answer that the unit doesn't run counts: a systemctl that answers nothing proves nothing, and after a
    # silent answer, until one says the unit still runs, an answer past the 10 seconds can't tell when the unit ended
    while [ $((SECONDS - started)) -lt 20 ]; do
        state=0
        unit_running minerbot-selftest || state=$?
        [ "$state" != 2 ] || silent=yes
        [ "$state" != 0 ] || silent=no
        [ "$state" != 1 ] || break
        sleep 1
    done
    if [ "$state" = 2 ] || { [ "$silent" = yes ] && [ $((SECONDS - started)) -gt 10 ]; }; then
        die "systemctl didn't answer in time whether the self-test unit still runs, so the detached runs can't be trusted. Nothing was changed."
    fi
    if [ "$state" != 1 ] || [ $((SECONDS - started)) -gt 10 ]; then
        die "systemd doesn't enforce time limits here, so the detached runs can't be trusted. Nothing was changed."
    fi
    echo "It does: the self-test unit was gone within $((SECONDS - started)) seconds."
}

needed_commands() {   # every command the scripts use, there before anything changes
    local name
    local -a missing=()
    for name in "${needed[@]}"; do
        command -v "$name" > /dev/null || missing+=("$name")
    done
    for name in /usr/sbin/sendmail /scripts/restartsrv_nginx; do
        [ -x "$name" ] || missing+=("$name")
    done
    if [ ${#missing[@]} -gt 0 ]; then
        die "These commands the scripts use are missing: ${missing[*]}. Install them first. Nothing was changed."
    fi
}

foreground_part() {
    local done_line=''
    step="checking before it starts"
    trap 'status=$?; foreground_cleanup || true; exit "$status"' EXIT
    [ "$(id -u)" = 0 ] || die "Run this as root. Nothing was changed."
    if [ ! -d "$home" ] || [ "$(stat -c %a "$home")" != 700 ]; then
        die "$home must exist, with mode 700. Nothing was changed."
    fi
    [ -f "$home/bot.env" ] || die "$home/bot.env is missing, so this isn't the server's MinerBot folder. Nothing was changed."
    cd "$home"
    needed_commands
    if [ -f "$home/state/bootstrap.done" ]; then
        IFS= read -r -t 5 done_line < "$home/state/bootstrap.done" || true
        die "The move was done on ${done_line%% *} at ${done_line#* }. Nothing was changed."
    fi
    lock_is_free || die "Another run holds the lock: $(lock_holder). Wait for it to finish, then try again. Nothing was changed."
    self_test
    step="starting the move"
    if run_detached bootstrap 900 "$stage/deploy/bootstrap.sh" --apply "$commit" "$stage"; then
        exit 0
    fi
    exit 1
}

# ---- The detached part: the unit minerbot-bootstrap, which writes only to its log

bot_state() {   # the bot's container ID, start time and Compose's config label
    run_for 30 docker container inspect \
        -f '{{.Id}} {{.State.StartedAt}} {{index .Config.Labels "com.docker.compose.config-hash"}}' "$bot_container"
}

keep_old_files() {   # each of today's files into old/, once; a copy to a temporary name, then a rename
    local name installed=no
    if [ -f "$home/state/deploy-files.commit" ]; then
        installed=yes
    fi
    for name in "${old_files[@]}"; do
        if [ -e "$home/old/$name" ]; then
            continue
        fi
        if [ "$installed" = yes ]; then
            echo "Warning: old/$name is missing, and the new deploy files are installed already, so it's never made now: $home/$name may be new."
            continue
        fi
        if [ ! -e "$home/$name" ]; then
            echo "Today's $name isn't in $home, so there's nothing to keep of it."
            continue
        fi
        cp -p "$home/$name" "$home/old/.$name.tmp"
        mv -f "$home/old/.$name.tmp" "$home/old/$name"
        echo "Kept today's $name in old/."
    done
}

database_of() {   # database_of <setting>: the Database= part of one of bot.env's connection strings
    local cs part
    local -a parts=()
    cs=$(env_value "$home/bot.env" "$1") || return 1
    IFS=';' read -r -t 5 -a parts <<< "$cs" || true
    for part in "${parts[@]}"; do
        part=${part#"${part%%[! ]*}"}
        if [[ ${part,,} == database=* ]]; then
            printf '%s\n' "${part#*=}"
            return 0
        fi
    done
    echo "bot.env's $1 has no Database= part." >&2
    return 1
}

write_server_conf() {   # server.conf from bot.env's two connection strings, unless it exists
    local site bot n tmp
    if [ -f "$home/server.conf" ]; then
        echo "server.conf exists already. It's checked instead."
        return 0
    fi
    site=$(database_of ConnectionStrings__DefaultConnection) || die "The website's database can't be taken from bot.env (above). Nothing more was changed."
    bot=$(database_of ConnectionStrings__BotConnection) || die "MinerBot's database can't be taken from bot.env (above). Nothing more was changed."
    [[ $site =~ ^[A-Za-z0-9_]{1,64}$ ]] \
        || die "The database in bot.env's ConnectionStrings__DefaultConnection must be 1 to 64 letters, digits and _. Nothing more was changed."
    [[ $bot =~ ^[A-Za-z0-9_]{1,64}$ ]] \
        || die "The database in bot.env's ConnectionStrings__BotConnection must be 1 to 64 letters, digits and _. Nothing more was changed."
    n=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name IN ('$site', '$bot')") \
        || die "MariaDB didn't answer as root (above). Nothing more was changed."
    [ "$n" = 2 ] || die "The two databases bot.env names aren't both on this server. Nothing more was changed."
    tmp=$(mktemp "$home/.server.conf.XXXXXX")
    {
        echo "# This server's own names, which never go into the repository. bootstrap.sh wrote this file from bot.env."
        echo "# The scripts take it line by line as text and never run it. server.conf.example in the repository explains each line."
        printf 'SITE_DB=%s\nBOT_DB=%s\nBOT_READY_PATTERN=%s\n' "$site" "$bot" "Gateway +Ready"
    } > "$tmp"
    chmod 600 "$tmp"
    mv -f "$tmp" "$home/server.conf"
    echo "Wrote server.conf: SITE_DB=$site, BOT_DB=$bot. The names stay on this server."
}

reserve_1654() {   # UID and GID 1654, the containers' user, for a system user that can't sign in
    local user group
    user=$(getent passwd 1654 | cut -d: -f1) || user=
    group=$(getent group 1654 | cut -d: -f1) || group=
    if [ "$user" = minerbot-app ] && [ "$group" = minerbot-app ]; then
        echo "UID and GID 1654 are reserved already."
        return 0
    fi
    if [ -z "$user" ] && { [ -z "$group" ] || [ "$group" = minerbot-app ]; }; then
        if [ -z "$group" ] && ! run_for 30 groupadd --system --gid 1654 minerbot-app; then
            echo "Warning: groupadd couldn't reserve GID 1654 (above). The rest goes on, and status.sh keeps saying so."
            return 0
        fi
        if ! run_for 30 useradd --system --uid 1654 --gid 1654 --no-create-home --home-dir /nonexistent \
            --shell /sbin/nologin --comment "MinerBot containers (reserved)" minerbot-app; then
            echo "Warning: useradd couldn't reserve UID 1654 (above). The rest goes on, and status.sh keeps saying so."
            return 0
        fi
        echo "Reserved UID and GID 1654 as minerbot-app, a system user that can't sign in, so no future account gets the ID both containers run as."
        return 0
    fi
    echo "Warning: UID 1654 belongs to ${user:-nobody} and GID 1654 to ${group:-nobody}, so they weren't reserved. Both containers run as 1654, and whoever has it could see their processes. The rest goes on, and status.sh keeps saying so."
}

dry_run_check() {   # dry_run_check <the file, in words> <compose file> <message when Compose would recreate the bot>
    local s=0
    bot_unchanged "$2" || s=$?
    [ "$s" != 0 ] || return 0
    if [ -n "${dry_run:-}" ]; then
        printf '%s\n' "$dry_run" | sed 's/^/  /'
    fi
    case $s in
        1) die "$3" ;;
        2)
            case ${dry_run_status:-} in
                124 | 137) die "Compose's dry run with $1 didn't finish within 60 seconds, so it said nothing about the bot. Nothing was installed, and the bot wasn't touched. Run the same commands again." ;;
                *) die "Compose's dry run with $1 failed with exit code ${dry_run_status:-unknown} (its lines, if any, are above), so it said nothing about the bot. Nothing was installed, and the bot wasn't touched. Run the same commands again once Docker and Compose answer." ;;
            esac
            ;;
        *) die "Compose's dry run with $1 printed lines this check doesn't recognise (above, if any), perhaps from a newer Compose, so it can't tell whether the bot would stay as it runs. Nothing was installed, and the bot wasn't touched. Decide with the owner what those lines mean first." ;;
    esac
}

check_bot_unchanged() {   # Compose's own dry run, with today's compose.yml and then with the new one
    local hash_old hash_new
    [ -f "$home/old/compose.yml" ] \
        || die "Today's compose.yml isn't in old/, so the bot can't be checked. Nothing was installed, and the bot wasn't touched."
    dry_run_check "today's compose.yml" "$home/old/compose.yml" \
        "Compose would recreate the bot even with today's compose.yml, so bot.env was edited after the bot started, or Compose was upgraded. Nothing was installed, and the bot wasn't touched. Decide with the owner whether to recreate the bot on purpose first."
    echo "Compose's dry run with today's compose.yml: the bot stays exactly as it runs."
    dry_run_check "the new compose.yml" "$stage/deploy/compose.yml" \
        "The new compose.yml would recreate the bot (Compose's lines are above). Nothing was installed, and the bot wasn't touched."
    echo "Compose's dry run with the new compose.yml: the bot stays exactly as it runs."
    hash_old=$(run_for 30 docker compose -f "$home/old/compose.yml" --project-directory "$home" config --hash bot) \
        || die "Compose couldn't parse today's compose.yml (above). Nothing was installed."
    hash_new=$(run_for 30 docker compose -f "$stage/deploy/compose.yml" --project-directory "$home" config --hash bot) \
        || die "Compose couldn't parse the new compose.yml (above). Nothing was installed."
    [ "$hash_old" = "$hash_new" ] \
        || die "Compose's config hash for the bot differs between today's compose.yml and the new one. Nothing was installed, and the bot wasn't touched."
    echo "Compose's config hash for the bot is the same for both files."
}

remove_one_offs() {   # setup-login.sh holds real names and already ran; move-docker-storage.sh must never run again
    local name
    for name in setup-login.sh move-docker-storage.sh; do
        if [ -e "$home/$name" ] && [ -e "$home/old/$name" ]; then
            rm -f "$home/$name"
            echo "Removed $name from $home. Its copy is in old/."
        fi
    done
}

detached_part() {
    local before after left tmp
    if [ "${MINERBOT_DETACHED:-}" != 1 ] || [ -z "${MINERBOT_LOG:-}" ]; then
        die "--apply is only for the run bootstrap.sh starts itself. Nothing was changed."
    fi
    exec >> "$MINERBOT_LOG" 2>&1
    run_mode=detached

    # 1. The lock, the leftovers of a run that was cut off, and the bot as it is
    step="taking the lock"
    take_lock bootstrap
    trap 'status=$?; rm -rf "$stage" || true; exit "$status"' EXIT
    cd "$home"
    echo "The move to the deploy files of $rev, as the unit minerbot-bootstrap, $(date -u +%Y-%m-%dT%H:%M:%SZ)."
    for left in "$home"/state/tmp/bootstrap.*; do
        if [ -e "$left" ] && [ "$left" != "$stage" ]; then
            rm -rf "$left"
        fi
    done
    step="recording the bot's state"
    before=$(bot_state) || die "Docker didn't answer about the bot (above). Nothing was changed."
    echo "The bot's container: $before"
    cut_point 1

    # 2.
    step="making the folders"
    install -d -m 700 "$home/state" "$home/logs" "$home/old"
    cut_point 2

    # 3.
    step="keeping today's files"
    keep_old_files
    cut_point 3

    # 4.
    step="writing server.conf"
    write_server_conf
    load_server_conf
    cut_point 4

    # 5.
    step="reserving UID and GID 1654"
    reserve_1654
    cut_point 5

    # 6.
    step="asking Compose about the bot"
    check_bot_unchanged
    step="installing the deploy files"
    install_deploy_files "$commit" "$stage/deploy"
    cut_point 6

    # 7.
    step="removing the one-off scripts"
    remove_one_offs
    cut_point 7

    # 8.
    step="checking the bot again"
    after=$(bot_state) || die "Docker didn't answer about the bot (above)."
    [ "$after" = "$before" ] \
        || die "The bot's container changed during the move, from $before to $after. The move itself never touches it: find out what restarted it before anything else."
    echo "The bot's container is exactly as before: the same ID, start time and label."
    cut_point 8

    # 9.
    step="finishing"
    tmp=$(mktemp "$home/state/.bootstrap.done.XXXXXX")
    printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$commit" > "$tmp"
    chmod 600 "$tmp"
    mv -f "$tmp" "$home/state/bootstrap.done"
    echo "The move is done, and the bot kept running. Backups start tonight. Next: ./deploy.sh main --only dashboard --no-start"
    finish ok "moved to the new deploy files"
}

if [ "$apply" = yes ]; then
    detached_part
else
    foreground_part
fi

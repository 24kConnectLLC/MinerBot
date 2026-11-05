# Shared by MinerBot's scripts in /opt/minerbot. It holds constants and functions only: sourcing it sets home, PATH and
# the other constants, and runs no command. From deploy/common.sh in the MinerBot repository; deploy.sh installs it as
# /opt/minerbot/common.sh, so change it there. Each script sources the copy in its own folder.
# shellcheck shell=bash disable=SC2034

# ---- Constants

home=/opt/minerbot
bot_container=minerbot-bot
dashboard_container=minerbot-dashboard
public_host=bot.murderminershub.com
repository_url=https://github.com/24kConnectLLC/MinerBot
# The dashboard's database login, on both its hosts. No cPanel account name or database prefix can hold a hyphen, so
# cPanel never takes it for one of an account's logins.
dashboard_login=minerbot-dashboard
# A locked login that exists only while the rights file is rehearsed on it
check_login=minerbot-rights-check
grantees="'$dashboard_login'@'localhost', '$dashboard_login'@'127.0.0.1'"
# What MinerBot puts into nginx: one server file in conf.d, and a folder only root can open for the certificate, its key
# and the proxy key
nginx_conf=/etc/nginx/conf.d/minerbot-dashboard.conf
nginx_secrets=/etc/nginx/minerbot
nginx_names=(minerbot-dashboard.conf origin-cert.pem origin-key.pem proxy-key.conf)
# cPanel's own list of Cloudflare's addresses, which cPanel keeps up to date
cloudflare_list=/etc/nginx/conf.d/includes-optional/cloudflare.conf
backups=/srv/backups/minerbot
# The usual order, /usr/local first, which is also where scripts-test.sh puts its stand-ins
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# ---- Limits

# Runs a command with a time limit: at the limit, timeout sends TERM, and KILL <grace> seconds later (10 by default).
# Every call to Docker, MariaDB, curl, git, OpenSSL, nginx, ss, sendmail, systemctl, systemd-run and cPanel's
# /scripts/restartsrv_nginx in these scripts goes through it.
run_for() {   # run_for [-k <grace>] <seconds> <command...>
    local grace=10
    if [ "$1" = -k ]; then
        grace=$2
        shift 2
    fi
    timeout -k "$grace" "$@"
}

# Seconds left before MINERBOT_DEADLINE, the moment 30 seconds before systemd ends this run. Outside a detached run there
# is no deadline, and it prints a large number.
time_left() {
    if [[ ${MINERBOT_DEADLINE:-} =~ ^[0-9]+$ ]]; then
        echo $((MINERBOT_DEADLINE - $(date +%s)))
    else
        echo 999999
    fi
}

# The test seam for a run cut off between two steps: nothing happens unless MINERBOT_TEST_CUT_AFTER names this step. A
# real unit never has that variable, because run_detached passes only its own --setenv values.
cut_point() {   # cut_point <step>
    [ "${MINERBOT_TEST_CUT_AFTER:-}" = "$1" ] || return 0
    echo "Cut off after step $1 (MINERBOT_TEST_CUT_AFTER)."
    kill -KILL "$$"
}

# How long each question waits for a line, 10 minutes. scripts-test.sh can shorten it with MINERBOT_TEST_READ_LIMIT.
read_limit() {
    if [[ ${MINERBOT_TEST_READ_LIMIT:-} =~ ^[0-9]+$ ]]; then
        echo "$MINERBOT_TEST_READ_LIMIT"
    else
        echo 600
    fi
}

# True when scripts-test.sh asks for short waits with MINERBOT_TEST_FAST=1. A real unit never has that variable.
test_fast() {
    [ "${MINERBOT_TEST_FAST:-}" = 1 ]
}

# ---- Messages and history

# Stops the script with a message that says what happened, what state it left and what to do. A script can define
# before_stop, which runs once first, to put back what it started changing. A detached run also writes its closing line
# in logs/history.log and "RESULT failed" for the terminal that follows it. In a subshell, such as $(...), only the
# subshell stops, and its caller stops in turn through set -e and its ERR trap.
die() {   # die <message>
    printf '%s\n' "$*" >&2
    if [ "$BASHPID" = "$$" ]; then
        if [ "${before_stop_done:-}" != yes ] && declare -F before_stop > /dev/null; then
            before_stop_done=yes
            before_stop || true
        fi
        if [ "${run_mode:-}" = detached ]; then
            log_history "${action:-run}" "${subject:--}" "${rev:--}" failed "stopped while ${step:-starting}" || true
            echo "RESULT failed"
        fi
    fi
    exit 1
}

# The ERR trap of every script: a command failed that the script didn't expect to fail. In a subshell it does nothing,
# because the subshell's caller sees the failure and stops itself.
stopped() {   # stopped <exit code> <line>
    [ "$BASHPID" = "$$" ] || return 0
    die "${0##*/} stopped while ${step:-starting} (line $2, exit code $1).${stop_hint:+ $stop_hint}"
}

# The run's closing line in logs/history.log and its RESULT line, then the exit: 1 when the word is "failed", else 0
finish() {   # finish <ok|built|failed> [<note>]
    local word=$1
    shift
    log_history "${action:-run}" "${subject:--}" "${rev:--}" "$word" "$@" || true
    if [ "$word" = failed ]; then
        echo "RESULT failed"
        exit 1
    fi
    echo "RESULT ok"
    exit 0
}

# One tab-separated line in logs/history.log: the time, the fields, and the run's log when there is one, as in
# 2026-11-06T03:12:44Z  deploy  dashboard  1a2b3c4d5e6f  ok  was 0f1e2d3c4b5a  log=logs/20261106T030832Z-deploy.log
log_history() {   # log_history <field>...
    local line field
    line=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    for field in "$@"; do
        line+=$'\t'$field
    done
    if [ -n "${MINERBOT_LOG:-}" ]; then
        line+=$'\t'log=${MINERBOT_LOG#"$home"/}
    fi
    install -d -m 700 "$home/logs"
    printf '%s\n' "$line" >> "$home/logs/history.log"
}

# ---- Locks

# One deploy, rollback, setup, bootstrap, fill-secrets or nightly backup changes things at a time. The lock is on fd 9 and
# lasts as long as the process that holds it, however it ends. lock.info says who holds it.
take_lock() {   # take_lock <who> [<seconds to wait, 10 by default>] [<message when it can't>]
    install -d -m 700 "$home/state"
    exec 9>> "$home/state/lock"
    if ! flock -w "${2:-10}" 9; then
        die "${3:-Another run holds the lock: $(lock_holder). Wait for it to finish, then try again. Nothing was changed.}"
    fi
    printf '%s, started %s, log %s\n' "$1" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "${MINERBOT_LOG:-none}" > "$home/state/lock.info"
}

lock_is_free() {   # true when nothing holds the lock: it tries the lock and lets go at once
    [ -e "$home/state/lock" ] || return 0
    flock -n "$home/state/lock" true
}

lock_holder() {   # who holds the lock, from lock.info
    local info=
    if [ -f "$home/state/lock.info" ]; then
        IFS= read -r -t 5 info < "$home/state/lock.info" || true
    fi
    printf '%s\n' "${info:-unknown}"
}

# ---- server.conf

# This server's own names, from /opt/minerbot/server.conf, taken line by line as text and never run. Sets SITE_DB, BOT_DB
# and BOT_READY_PATTERN, and checks that both databases exist.
load_server_conf() {
    local count status=0
    [ -f "$home/server.conf" ] \
        || die "$home/server.conf is missing. bootstrap.sh writes it; server.conf.example in the repository shows its lines. Nothing was changed."
    SITE_DB=$(conf_value SITE_DB)
    BOT_DB=$(conf_value BOT_DB)
    BOT_READY_PATTERN=$(conf_value BOT_READY_PATTERN)
    BOT_READY_PATTERN=${BOT_READY_PATTERN:-Gateway +Ready}
    [[ $SITE_DB =~ ^[A-Za-z0-9_]{1,64}$ ]] \
        || die "SITE_DB in $home/server.conf must be the website's database name: 1 to 64 letters, digits and _. Nothing was changed."
    [[ $BOT_DB =~ ^[A-Za-z0-9_]{1,64}$ ]] \
        || die "BOT_DB in $home/server.conf must be MinerBot's own database name: 1 to 64 letters, digits and _. Nothing was changed."
    grep -qE -- "$BOT_READY_PATTERN" /dev/null || status=$?
    if [ "$status" != 1 ] || [ ${#BOT_READY_PATTERN} -gt 200 ]; then
        die "BOT_READY_PATTERN in $home/server.conf must be a pattern grep -E accepts, at most 200 characters. Nothing was changed."
    fi
    count=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name IN ('$SITE_DB', '$BOT_DB')") \
        || die "MariaDB didn't answer as root within 30 seconds (above). Nothing was changed."
    [ "$count" = 2 ] \
        || die "SITE_DB and BOT_DB in $home/server.conf must name two databases that exist on this server. Check both names. Nothing was changed."
}

conf_value() {   # conf_value <name>: the value of NAME= in server.conf (the last such line), or nothing
    local line value=
    [ -f "$home/server.conf" ] || return 0
    while IFS= read -r -t 5 line || [ -n "$line" ]; do
        if [[ $line == "$1="* ]]; then
            value=${line#"$1"=}
        fi
    done < "$home/server.conf"
    printf '%s\n' "$value"
}

# ---- Env files

# The one safe-character rule for a value in an env file: printable ASCII only; none of $, ", ', \ and the backtick; no
# space at the start or the end; no # right after a space; spaces inside a value are fine. Within it, Compose's env_file
# and docker run --env-file take every value the same way: Compose expands $, cuts at " #", trims the ends and strips
# outer quotes, and docker run --env-file does none of that.
env_safe() {   # env_safe <value>
    local LC_ALL=C value=$1
    [[ $value =~ ^[\ -~]*$ ]] || return 1
    case $value in
        *[\$\"\'\\\`]* | ' '* | *' ' | *' #'*) return 1 ;;
    esac
    return 0
}

env_rule="printable ASCII only; none of \$, \", ', \\ and the backtick; no space at the start or the end; no # right after a space; spaces inside a value are fine"

# The value of NAME in an env file, taken as text, without running anything: the last NAME= line wins, as with Compose,
# and one pair of outer quotes is stripped, as Compose does. A value that breaks the safe-character rule is refused with
# a message that names the setting but never its value. Prints nothing when the file or the name isn't there.
env_value() {   # env_value <file> <name>
    local file=$1 name=$2 line value='' found=no
    [ -f "$file" ] || return 0
    while IFS= read -r -t 5 line || [ -n "$line" ]; do
        if [[ $line == "$name="* ]]; then
            value=${line#"$name"=}
            found=yes
        fi
    done < "$file"
    [ "$found" = yes ] || return 0
    if [ ${#value} -ge 2 ] && { [[ $value == \"*\" ]] || [[ $value == \'*\' ]]; }; then
        value=${value:1:${#value}-2}
    fi
    if ! env_safe "$value"; then
        echo "$name in $file breaks the safe-character rule: $env_rule." >&2
        return 1
    fi
    printf '%s\n' "$value"
}

# Sets NAME=value in an env file and keeps every other line where it is. The new file is written beside the old one, 600,
# and renamed over it in one step. The value must pass env_safe.
write_env() {   # write_env <file> <name> <value>
    local file=$1 name=$2 value=$3 tmp line found=no
    env_safe "$value" || die "The value for $name breaks the safe-character rule ($env_rule), so $file wasn't changed."
    tmp=$(mktemp "$(dirname "$file")/.$(basename "$file").XXXXXX")
    {
        if [ -f "$file" ]; then
            while IFS= read -r -t 5 line || [ -n "$line" ]; do
                if [[ $line == "$name="* ]]; then
                    if [ "$found" = no ]; then
                        printf '%s=%s\n' "$name" "$value"
                    fi
                    found=yes
                else
                    printf '%s\n' "$line"
                fi
            done < "$file"
        fi
        if [ "$found" = no ]; then
            printf '%s=%s\n' "$name" "$value"
        fi
    } > "$tmp"
    chmod 600 "$tmp"
    mv -f "$tmp" "$file"
}

# ---- Detached runs

# Runs the script as a systemd unit of its own and follows its log here. The unit keeps running if this terminal or SSH
# session goes away; systemd ends it, every process it started included, at the limit; and a second unit of the same
# name can't start while it runs. The unit gets only these --setenv values, never this shell's environment.
run_detached() {   # run_detached <action> <limit seconds> <script> <arguments...>
    local action=$1 limit=$2 script=$3 stamp log n=1
    shift 3
    install -d -m 700 "$home/logs"
    # Always a new file, never one another run writes to: a second run of the same action in the same second gets -2
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    log=$home/logs/$stamp-$action.log
    until (set -o noclobber && umask 077 && : > "$log") 2> /dev/null; do
        n=$((n + 1))
        [ "$n" -le 9 ] || die "Couldn't make a new log for the $action in $home/logs. Nothing was changed."
        log=$home/logs/$stamp-$n-$action.log
    done
    chmod 600 "$log"
    run_for 30 systemd-run --quiet --collect --unit="minerbot-$action" -p RuntimeMaxSec="$limit" -p TimeoutStopSec=30 \
        --setenv=HOME=/root --setenv=MINERBOT_DETACHED=1 --setenv=MINERBOT_LOG="$log" \
        --setenv=MINERBOT_DEADLINE="$(($(date +%s) + limit - 30))" /bin/bash "$script" "$@" \
        || die "Couldn't start the $action as a unit of its own (above). Nothing was changed."
    detached_unit_started=yes
    echo "The $action runs on its own now: closing this window doesn't stop it. Its log: $log"
    follow "minerbot-$action" "$log" "$((limit + 60))"
}

unit_running() {   # unit_running <unit>: true while it runs, and while systemd is still stopping it
    case $(run_for 10 systemctl is-active "$1" 2> /dev/null) in
        active | activating | deactivating | reloading | refreshing) return 0 ;;
        *) return 1 ;;
    esac
}

# Shows the run's log as it grows until the unit ends, then returns 0 when its last RESULT line is "RESULT ok". Ctrl-C
# stops only the following: the run goes on. It looks every 2 seconds, or every 0.2 with MINERBOT_TEST_FAST=1. Where
# tail can't be told about changes (no inotify, as on overlayfs), it reads the log every half second, or tenth with
# MINERBOT_TEST_FAST=1, and gets a second, or 0.3, after the unit ends to show the last lines.
follow() {   # follow <unit> <log> <limit seconds>
    local unit=$1 log=$2 limit=$3 started=$SECONDS result poll=2 tail_poll=0.5 settle=1
    if test_fast; then
        poll=0.2 tail_poll=0.1 settle=0.3
    fi
    tail -s "$tail_poll" -n +1 -f "$log" &
    follow_tail=$!
    follow_note="The ${unit#minerbot-} goes on. Follow it again with: tail -f $log"
    trap 'follow_interrupted' INT TERM HUP
    while unit_running "$unit" && [ $((SECONDS - started)) -lt "$limit" ]; do
        sleep "$poll"
    done
    sleep "$settle"
    kill "$follow_tail" 2> /dev/null || true
    wait "$follow_tail" 2> /dev/null || true
    trap - INT TERM HUP
    result=$(grep -E '^RESULT (ok|failed)$' "$log" | tail -n 1) || result=
    case $result in
        "RESULT ok") return 0 ;;
        "RESULT failed") return 1 ;;
    esac
    if unit_running "$unit"; then
        echo "It still runs after $limit seconds, longer than its own limit. Look at it with: systemctl status $unit"
    else
        echo "It ended without a result: it was stopped at its time limit, or the server restarted. See the log: $log"
    fi
    return 1
}

follow_interrupted() {
    kill "$follow_tail" 2> /dev/null || true
    echo
    echo "$follow_note"
    exit 1
}

# ---- Images

container_of() {   # container_of bot|dashboard: the service's container
    if [ "$1" = bot ]; then
        printf '%s\n' "$bot_container"
    else
        printf '%s\n' "$dashboard_container"
    fi
}

# The ID of an image, or nothing when there's no such image. Any other failure stops the script, because a wrong answer
# here would make a swap take the wrong path.
image_id() {   # image_id <image or ID>
    local out status=0
    out=$(run_for 30 docker image inspect -f '{{.Id}}' "$1" 2>&1) || status=$?
    if [ "$status" = 0 ]; then
        printf '%s\n' "$out"
        return 0
    fi
    case $out in
        *"No such image"* | *"No such object"*) return 0 ;;
    esac
    printf '%s\n' "$out" >&2
    die "Docker didn't answer about the image $1 (above). Nothing more was changed."
}

# The ID of the image a container runs, or nothing when there's no such container
container_image() {   # container_image <container>
    local out status=0
    out=$(run_for 30 docker container inspect -f '{{.Image}}' "$1" 2>&1) || status=$?
    if [ "$status" = 0 ]; then
        printf '%s\n' "$out"
        return 0
    fi
    case $out in
        *"No such container"* | *"No such object"*) return 0 ;;
    esac
    printf '%s\n' "$out" >&2
    die "Docker didn't answer about the container $1 (above). Nothing more was changed."
}

# The first 12 characters of the commit an image was built from, from the label deploy.sh gives it; "image <short ID>"
# for an image without the label, such as the bot's images from before; "none" when there's no such image. For messages
# and history only, so it never stops anything.
revision() {   # revision <image or ID>
    local out id='' label=''
    if [ -z "$1" ]; then
        echo none
        return 0
    fi
    out=$(run_for 30 docker image inspect -f '{{.Id}} {{index .Config.Labels "org.opencontainers.image.revision"}}' "$1" 2> /dev/null) || out=
    read -r -t 5 id label <<< "$out" || true
    if [ -n "$label" ]; then
        printf '%s\n' "${label:0:12}"
    elif [ -n "$id" ]; then
        id=${id#sha256:}
        printf 'image %s\n' "${id:0:12}"
    else
        echo none
    fi
}

tag_id() {   # tag_id <image ID> <name:tag>
    run_for 30 docker tag "$1" "$2" || die "Couldn't tag $1 as $2 (above)."
}

untag() {   # untag <name:tag>: takes the tag away; the image stays while another tag or a container uses it
    [ -n "$(image_id "$1")" ] || return 0
    run_for 30 docker rmi "$1" > /dev/null || die "Couldn't take the tag $1 away (above)."
}

# ---- Services

compose() {   # compose <seconds> <compose arguments...>: Compose on the installed compose.yml, with a limit
    local limit=$1
    shift
    run_for "$limit" docker compose -f "$home/compose.yml" --project-directory "$home" "$@"
}

# Creates or starts a service on its :current image. Compose recreates the container when the image behind :current or
# the service's settings changed, and only starts a stopped one otherwise.
start_service() {   # start_service bot|dashboard [--force-recreate]
    if [ "${2:-}" = --force-recreate ]; then
        compose 120 up -d --no-build --no-deps --force-recreate "$1"
    else
        compose 120 up -d --no-build --no-deps "$1"
    fi
}

ready_service() {   # ready_service bot|dashboard [<public status before the swap>] [<restart count it starts from>]
    if [ "$1" = bot ]; then
        ready_bot "${3:-0}"
    else
        ready_dashboard "${2:-}" "${3:-0}"
    fi
}

# Waits until the dashboard is ready, for 90 seconds: its container runs with no new restart and isn't unhealthy;
# /health/ready answers Healthy, which proves both databases with its own login and rights; and the process listening on
# 127.0.0.1:5080 is the container's own, so a process that took the port can't pass for it. Then, when the public home
# page answered 200 before the swap, it must answer 200 again within 30 seconds.
ready_dashboard() {   # ready_dashboard [<public status before the swap>] [<restart count it starts from, 0 by default>]
    local public_before=${1:-} base=${2:-0} limit=90 public_limit=30 poll=2 started=$SECONDS
    local state running restarts health pid said listeners why
    if test_fast; then
        limit=10 public_limit=4 poll=0.5
    fi
    while :; do
        why=
        state=$(run_for 30 docker container inspect \
            -f '{{.State.Running}} {{.RestartCount}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.State.Pid}}' \
            "$dashboard_container" 2> /dev/null) || state=
        running='' restarts='' health='' pid=''
        read -r -t 5 running restarts health pid <<< "$state" || true
        if [ -z "$state" ]; then
            why="its container doesn't exist"
        elif [ "$restarts" != "$base" ]; then
            echo "The dashboard isn't ready: its container restarted (restart count $restarts)."
            return 1
        elif [ "$health" = unhealthy ]; then
            echo "The dashboard isn't ready: Docker's health check says unhealthy."
            return 1
        elif [ "$running" != true ]; then
            why="its container isn't running"
        else
            said=$(run_for 10 curl -s --max-time 5 -H 'Host: localhost' http://127.0.0.1:5080/health/ready 2> /dev/null) || said=
            if [ "$said" != Healthy ]; then
                why="/health/ready answers \"${said:0:40}\""
            else
                listeners=$(run_for 10 ss -Hltnp 'sport = :5080' 2> /dev/null) || listeners=
                if listener_is "$pid" "$listeners"; then
                    break
                fi
                why="the process listening on 127.0.0.1:5080 isn't the dashboard's container"
            fi
        fi
        if [ $((SECONDS - started)) -ge "$limit" ]; then
            echo "The dashboard wasn't ready within $limit seconds: $why."
            return 1
        fi
        sleep "$poll"
    done
    echo "The dashboard is ready: /health/ready answers Healthy, and port 5080 is its own."
    [ "$public_before" = 200 ] || return 0
    started=$SECONDS
    until [ "$(public_status)" = 200 ]; do
        if [ $((SECONDS - started)) -ge "$public_limit" ]; then
            echo "The public home page, https://$public_host/, answered 200 before the swap but not within $public_limit seconds after it."
            return 1
        fi
        sleep "$poll"
    done
    echo "The public home page answers 200, as before the swap."
}

listener_is() {   # listener_is <pid> <lines of ss -Hltnp>: true when there's a listener and each one is that process
    local pid=$1 line seen=no
    if ! [[ $pid =~ ^[1-9][0-9]*$ ]]; then
        return 1
    fi
    while IFS= read -r -t 5 line; do
        [ -n "$line" ] || continue
        if [[ $line != *"pid=$pid,"* ]]; then
            return 1
        fi
        seen=yes
    done <<< "$2"
    [ "$seen" = yes ]
}

# Waits until the bot is up. Once its image has a health check, that means healthy. Until then, which is until PR 6
# gives it a heartbeat, it means: its log since it started has a line matching BOT_READY_PATTERN from server.conf within
# 150 seconds, and 20 seconds later it still runs with the same start time and no new restart. The log goes to a file
# only root can open, never to the screen, because it can hold a Steam sign-in code.
ready_bot() {   # ready_bot [<restart count it starts from, 0 by default>]
    local base=${1:-0} limit=150 steady=20 poll=3 started=$SECONDS pattern=${BOT_READY_PATTERN:-Gateway +Ready}
    local state running restarts since health log count why started_again
    if test_fast; then
        limit=10 steady=1 poll=0.5
    fi
    install -d -m 700 "$home/state" "$home/state/tmp"
    log=$(mktemp "$home/state/tmp/bot-log.XXXXXX") || return 1
    while :; do
        why=
        state=$(run_for 30 docker container inspect \
            -f '{{.State.Running}} {{.RestartCount}} {{.State.StartedAt}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
            "$bot_container" 2> /dev/null) || state=
        running='' restarts='' since='' health=''
        read -r -t 5 running restarts since health <<< "$state" || true
        if [ -z "$state" ]; then
            why="its container doesn't exist"
        elif [ "$restarts" != "$base" ]; then
            echo "The bot isn't ready: its container restarted (restart count $restarts)."
            rm -f "$log"
            return 1
        elif [ "$health" = unhealthy ]; then
            echo "The bot isn't ready: Docker's health check says unhealthy."
            rm -f "$log"
            return 1
        elif [ "$running" != true ]; then
            why="its container isn't running"
        elif [ "$health" = healthy ]; then
            break
        elif [ "$health" != none ]; then
            why="its health check says $health"
        else
            count=0
            if run_for 30 docker logs --since "$since" "$bot_container" > "$log" 2>&1; then
                count=$(grep -cE -- "$pattern" "$log") || count=0
            fi
            if [ "$count" -ge 1 ]; then
                break
            fi
            why="its log has no line matching \"$pattern\" yet"
        fi
        if [ $((SECONDS - started)) -ge "$limit" ]; then
            echo "The bot wasn't ready within $limit seconds: $why."
            rm -f "$log"
            return 1
        fi
        sleep "$poll"
    done
    rm -f "$log"
    echo "The bot says it's ready. Checking that it still runs in $steady seconds."
    sleep "$steady"
    state=$(run_for 30 docker container inspect -f '{{.State.Running}} {{.RestartCount}} {{.State.StartedAt}}' "$bot_container" 2> /dev/null) || state=
    running='' restarts='' started_again=''
    read -r -t 5 running restarts started_again _ <<< "$state" || true
    if [ "$running" != true ] || [ "$restarts" != "$base" ] || [ "$started_again" != "$since" ]; then
        echo "The bot didn't stay up: it stopped or restarted within $steady seconds of saying it was ready."
        return 1
    fi
    echo "The bot is ready, and still runs."
}

# The public home page's HTTP status through Cloudflare, or 000 when nothing answered
public_status() {
    local code
    code=$(run_for 15 curl -s -o /dev/null -w '%{http_code}' --max-time 10 "https://$public_host/" 2> /dev/null) || true
    printf '%s\n' "${code:-000}"
}

# Asks Compose itself whether "up" with this compose file would leave the bot exactly as it runs. Its dry run changes
# nothing. It passes only when exactly one line says the bot's container is Running and no line plans a change, so output
# it doesn't recognise, from a newer Compose say, refuses. Compose's lines are left in $dry_run for the message.
bot_unchanged() {   # bot_unchanged <compose file>
    local line words running=0
    local -a parts
    dry_run=$(run_for 60 docker compose -f "$1" --project-directory "$home" up --dry-run -d --no-build --no-deps bot 2>&1) \
        || return 1
    while IFS= read -r -t 5 line; do
        parts=()
        read -r -t 5 -a parts <<< "$line" || true
        words=${parts[*]:-}
        [ -n "$words" ] || continue
        case $words in
            *Recreate* | *Create* | *Start* | *Stop* | *Remove* | *Kill* | *Pull* | *Build*) return 1 ;;
        esac
        if [ "$words" = "Container $bot_container Running" ]; then
            running=$((running + 1))
        fi
    done <<< "$dry_run"
    [ "$running" = 1 ]
}

# ---- Migrations and settings

# "yes" when the commit has a migration MinerBot's database doesn't have yet, else "no": the highest NNNN_ file in
# DataAccessLibrary/Migrations against schema_version's highest version, which is 0 before that table exists
migrations_due() {   # migrations_due <commit>
    local names name number newest=0 tables applied=0
    names=$(run_for 30 git -C "$home/src" ls-tree --name-only "$1" DataAccessLibrary/Migrations/) \
        || die "git couldn't list the migrations of ${1:0:12} (above). Nothing was swapped."
    while IFS= read -r -t 5 name; do
        name=${name##*/}
        if [[ $name =~ ^([0-9]{4})_.+\.sql$ ]]; then
            number=$((10#${BASH_REMATCH[1]}))
            if [ "$number" -gt "$newest" ]; then
                newest=$number
            fi
        fi
    done <<< "$names"
    tables=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$BOT_DB' AND table_name = 'schema_version'") \
        || die "MariaDB didn't answer (above). Nothing was swapped."
    if [ "$tables" = 1 ]; then
        applied=$(run_for 30 mariadb -N -e "SELECT COALESCE(MAX(version), 0) FROM \`$BOT_DB\`.schema_version") \
            || die "MariaDB didn't answer (above). Nothing was swapped."
    fi
    [[ $applied =~ ^[0-9]+$ ]] || die "schema_version's highest version isn't a number. Nothing was swapped."
    if [ "$newest" -gt "$applied" ]; then
        echo yes
    else
        echo no
    fi
}

# Runs the migrations in the new dashboard image with the bot's own login, which can create tables. Only that one
# setting goes in: bot.env's value is taken as text, put into this one command's environment and passed on by name, so it
# never shows in the process list. It isn't docker compose run, which would load dashboard.env with the wrong login.
run_migrate() {
    local bot_connection
    bot_connection=$(env_value "$home/bot.env" ConnectionStrings__BotConnection) \
        || die "bot.env's ConnectionStrings__BotConnection can't be used as it is (above). Nothing was migrated or swapped."
    [ -n "$bot_connection" ] \
        || die "bot.env has no ConnectionStrings__BotConnection, which the migrations need. Nothing was migrated or swapped."
    # A run killed at its limit can leave the container behind, with the anonymous volume the image's VOLUME /app/keys
    # gives it. -v removes that too.
    run_for 30 docker rm -f -v minerbot-migrate > /dev/null 2>&1 || true
    if ! ConnectionStrings__BotConnection=$bot_connection run_for 180 docker run --rm --pull never --name minerbot-migrate \
        --network host --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --cap-drop ALL \
        --security-opt no-new-privileges:true --memory 256m --pids-limit 64 \
        -e ConnectionStrings__BotConnection minerbot-dashboard:new migrate; then
        run_for 30 docker rm -f -v minerbot-migrate > /dev/null 2>&1 || true
        die "The migrations didn't finish (above). Nothing was swapped. Migrations only ever add, so what runs keeps working."
    fi
    run_for 30 docker rm -f -v minerbot-migrate > /dev/null 2>&1 || true
}

# The dashboard's own validator on an env file: the image's check-settings command, which exits 0 when the dashboard
# would start and 1 with the list, naming settings but never their values. Nothing listens, nothing connects, and the
# keys volume isn't touched.
check_settings() {   # check_settings <env file> <image>
    local status=0
    run_for 30 docker rm -f -v minerbot-dashboard-check > /dev/null 2>&1 || true
    run_for 60 docker run --rm --pull never --name minerbot-dashboard-check --network none --read-only \
        --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --tmpfs /app/keys:rw,noexec,nosuid,nodev,size=1m,mode=1777 \
        --cap-drop ALL --security-opt no-new-privileges:true --memory 256m --pids-limit 64 \
        --env-file "$1" "$2" check-settings || status=$?
    run_for 30 docker rm -f -v minerbot-dashboard-check > /dev/null 2>&1 || true
    return "$status"
}

# ---- Rights

# The rights file with this server's names filled in: {{SITE_DB}}, {{BOT_DB}} and {{GRANTEES}}, by bash itself. It keeps
# the line count, so MariaDB's "at line N" is the file's line N, and refuses when any {{ is left.
render() {   # render <file> <grantees>
    local text
    if ! [[ ${SITE_DB:-} =~ ^[A-Za-z0-9_]{1,64}$ && ${BOT_DB:-} =~ ^[A-Za-z0-9_]{1,64}$ ]]; then
        echo "The database names from server.conf aren't loaded, so $1 can't be filled in." >&2
        return 1
    fi
    text=$(< "$1") || return 1
    text=${text//"{{SITE_DB}}"/"$SITE_DB"}
    text=${text//"{{BOT_DB}}"/"$BOT_DB"}
    text=${text//"{{GRANTEES}}"/"$2"}
    if [[ $text == *"{{"* ]]; then
        echo "${1##*/} has a {{...}} placeholder these scripts don't fill. Nothing was applied." >&2
        return 1
    fi
    printf '%s\n' "$text"
}

# Every right one login has, one per line and sorted, without the login's name, so two logins compare with diff. It
# never uses SHOW GRANTS, which prints the password's hash.
rights_of() {   # rights_of <user> <host>
    local sql who
    sql=$(< "$home/rights-list.sql") || return 1
    who="''$1''@''$2''"
    sql=${sql//"{{WHO}}"/"$who"}
    run_for 30 mariadb --batch -N <<< "$sql" | LC_ALL=C sort
}

drop_rights_check() {   # drops the rehearsal's throwaway login
    if run_for 30 mariadb --batch -e "DROP USER IF EXISTS '$check_login'@'localhost'"; then
        rights_check_made=no
    else
        echo "Couldn't drop the throwaway login $check_login (above). It's locked, so it can't sign in; the next rights run drops it."
        return 1
    fi
}

# Applies the rights file to the dashboard's login, as deploy/README.md describes: a rehearsal on a locked throwaway
# login first, then the real login on both its hosts, then a comparison of the two. It says why when it fails: a failed
# rehearsal leaves the real login as it was, a failed apply leaves it fewer rights and never more, and a difference means
# someone added a right REVOKE can't take away, such as a PROXY grant, which a person must remove.
apply_rights() {   # apply_rights [<rights file>, the installed dashboard-rights.sql by default]
    local file=${1:-$home/dashboard-rights.sql} rehearsal real expected actual host
    rehearsal=$(render "$file" "'$check_login'@'localhost'") || return 1
    real=$(render "$file" "$grantees") || return 1
    rights_check_made=yes
    if ! run_for 30 mariadb --batch -e "DROP USER IF EXISTS '$check_login'@'localhost'; CREATE USER '$check_login'@'localhost' ACCOUNT LOCK;"; then
        echo "Couldn't make the throwaway login for the rehearsal (above). The dashboard's login wasn't touched."
        drop_rights_check || true
        return 1
    fi
    if ! run_for 60 mariadb --batch <<< "$rehearsal"; then
        echo "The rehearsal of ${file##*/} on a throwaway login failed (above; its line is the file's line), so the dashboard's login wasn't touched."
        drop_rights_check || true
        return 1
    fi
    if ! run_for 60 mariadb --batch <<< "$real"; then
        echo "Applying ${file##*/} to the dashboard's login failed (above), after a rehearsal that passed. The login may have fewer rights than the file gives, never more. Running this again finishes it."
        drop_rights_check || true
        return 1
    fi
    if ! expected=$(rights_of "$check_login" localhost); then
        echo "Couldn't list the throwaway login's rights (above)."
        drop_rights_check || true
        return 1
    fi
    drop_rights_check || true
    for host in localhost 127.0.0.1; do
        if ! actual=$(rights_of "$dashboard_login" "$host"); then
            echo "Couldn't list the dashboard's login's rights on $host (above)."
            return 1
        fi
        if [ "$expected" != "$actual" ]; then
            echo "The dashboard's login on $host doesn't have exactly the rights the file gives. Lines with > are rights it has besides them, which REVOKE can't take away, such as a PROXY grant: a person must remove them. Lines with < are rights it lacks:"
            diff <(printf '%s\n' "$expected") <(printf '%s\n' "$actual") || true
            return 1
        fi
    done
    echo "The database rights were rehearsed, applied and compared: the dashboard's login has exactly what ${file##*/} gives."
}

# ---- Deploy files

# Where each of a commit's deploy files goes on this server, and its mode. The nginx file is a reference copy: only
# setup-dashboard.sh puts it into nginx.
deploy_file_table() {
    printf '%s\n' \
        "compose.yml $home/compose.yml 600" \
        "common.sh $home/common.sh 600" \
        "dashboard-rights.sql $home/dashboard-rights.sql 600" \
        "rights-list.sql $home/rights-list.sql 600" \
        "minerbot-dashboard.conf $home/minerbot-dashboard.conf 600" \
        "deploy.sh $home/deploy.sh 700" \
        "rollback.sh $home/rollback.sh 700" \
        "setup-dashboard.sh $home/setup-dashboard.sh 700" \
        "fill-secrets.sh $home/fill-secrets.sh 700" \
        "backup.sh $home/backup.sh 700" \
        "status.sh $home/status.sh 700" \
        "README.txt $home/README.txt 600" \
        "minerbot.cron /etc/cron.d/minerbot 644"
}

# The checks a commit's deploy files pass before any of them is installed: every file is there; bash -n passes on every
# script; every script but common.sh, which only holds functions, has the lines set -Eeuo pipefail and umask 077; no
# script turns tracing on, which would print secrets; Compose accepts the compose file; and the rights file fills in.
check_deploy_files() {   # check_deploy_files <folder> <commit>
    local stage=$1 name out
    local -a problems=()
    for name in compose.yml common.sh dashboard-rights.sql rights-list.sql minerbot-dashboard.conf deploy.sh rollback.sh \
        setup-dashboard.sh fill-secrets.sh backup.sh status.sh bootstrap.sh README.txt minerbot.cron; do
        [ -f "$stage/$name" ] || problems+=("deploy/$name is missing.")
    done
    for name in common.sh deploy.sh rollback.sh setup-dashboard.sh fill-secrets.sh backup.sh status.sh bootstrap.sh; do
        [ -f "$stage/$name" ] || continue
        if ! out=$(bash -n "$stage/$name" 2>&1); then
            problems+=("deploy/$name fails bash -n: ${out//$'\n'/ }")
        fi
        if grep -Eq '^[[:space:]]*set[[:space:]]+-[A-Za-z]*x|x[t]race' "$stage/$name"; then
            problems+=("deploy/$name turns tracing on, which would print secrets.")
        fi
        [ "$name" != common.sh ] || continue
        grep -qx 'set -Eeuo pipefail' "$stage/$name" || problems+=("deploy/$name has no line set -Eeuo pipefail.")
        grep -qx 'umask 077' "$stage/$name" || problems+=("deploy/$name has no line umask 077.")
    done
    if [ -f "$stage/compose.yml" ] \
        && ! out=$(run_for 30 docker compose -f "$stage/compose.yml" --project-directory "$home" config --quiet 2>&1); then
        problems+=("Compose refuses deploy/compose.yml: ${out//$'\n'/ }")
    fi
    if [ -f "$stage/dashboard-rights.sql" ] && ! out=$(render "$stage/dashboard-rights.sql" "$grantees" 2>&1 > /dev/null); then
        problems+=("deploy/dashboard-rights.sql doesn't fill in: ${out//$'\n'/ }")
    fi
    if [ ${#problems[@]} -gt 0 ]; then
        printf '  %s\n' "${problems[@]}"
        die "The deploy files of ${2:0:12} didn't pass their checks (above), so none of them was installed. What runs is unchanged."
    fi
}

# Installs a commit's deploy files, as deploy/README.md describes: from the commit object, never from src's working tree;
# forward only, so deploying an older commit never takes newer scripts back; checked before anything is installed; and
# each changed file written beside its target and renamed over it, so a script that's running, this one included, keeps
# its old copy. Sets installed_files to the names it changed, and reexec_due to yes when deploy.sh or common.sh changed.
install_deploy_files() {   # install_deploy_files <commit> [<folder that holds that commit's deploy files>]
    local commit=$1 stage=${2:-} installed='' status=0 name target mode from tmp readme
    installed_files=()
    reexec_due=no
    if [ -f "$home/state/deploy-files.commit" ]; then
        IFS= read -r -t 5 installed < "$home/state/deploy-files.commit" || true
    fi
    if [ -n "$installed" ] && [ "$installed" != "$commit" ]; then
        run_for 10 git -C "$home/src" merge-base --is-ancestor "$installed" "$commit" || status=$?
        if [ "$status" = 1 ]; then
            echo "Kept the deploy files of ${installed:0:12}, which are newer than ${commit:0:12}."
            return 0
        fi
        [ "$status" = 0 ] \
            || die "git couldn't tell whether the installed deploy files, from ${installed:0:12}, are older than ${commit:0:12} (above). Nothing was installed."
    fi
    install -d -m 700 "$home/state" "$home/state/tmp"
    if [ -z "$stage" ]; then
        deploy_stage=$(mktemp -d "$home/state/tmp/deploy.XXXXXX")
        run_for 60 git -C "$home/src" archive "$commit" deploy | run_for 60 tar -x -C "$deploy_stage" \
            || die "Couldn't take the deploy files out of ${commit:0:12} (above). Nothing was installed."
        stage=$deploy_stage/deploy
    fi
    check_deploy_files "$stage" "$commit"
    readme=$(mktemp "$home/state/tmp/readme.XXXXXX")
    render_readme "$stage/README.txt" > "$readme"
    while IFS=' ' read -r -t 5 name target mode; do
        from=$stage/$name
        if [ "$name" = README.txt ]; then
            from=$readme
        fi
        if cmp -s "$from" "$target"; then
            if [ "$(stat -c %a "$target")" != "$mode" ]; then
                chmod "$mode" "$target"
            fi
            continue
        fi
        tmp=$(mktemp "$(dirname "$target")/.minerbot-install.XXXXXX")
        cp "$from" "$tmp"
        chmod "$mode" "$tmp"
        mv -f "$tmp" "$target"
        installed_files+=("$name")
    done < <(deploy_file_table)
    rm -f "$readme"
    tmp=$(mktemp "$home/state/.deploy-files.commit.XXXXXX")
    printf '%s\n' "$commit" > "$tmp"
    mv -f "$tmp" "$home/state/deploy-files.commit"
    if [ ${#installed_files[@]} -eq 0 ]; then
        echo "The deploy files are already those of ${commit:0:12}."
    else
        echo "Installed the deploy files of ${commit:0:12} that changed: ${installed_files[*]}."
    fi
    case " ${installed_files[*]} " in
        *" deploy.sh "* | *" common.sh "*) reexec_due=yes ;;
    esac
}

# README.txt as installed: the source with {{ORIGIN_CERT_ENDS}} filled in from the certificate nginx has, or "not
# installed yet"
render_readme() {   # render_readme <README.txt source>
    local text ends="not installed yet"
    text=$(< "$1")
    if [ -f "$nginx_secrets/origin-cert.pem" ]; then
        ends=$(cert_end "$nginx_secrets/origin-cert.pem") || ends="(unknown: openssl couldn't parse the certificate)"
    fi
    printf '%s\n' "${text//"{{ORIGIN_CERT_ENDS}}"/"$ends"}"
}

# True when nginx has MinerBot's file and the reference copy deploy.sh installed differs from it
nginx_file_differs() {
    [ -f "$nginx_conf" ] && [ -f "$home/minerbot-dashboard.conf" ] && ! cmp -s "$home/minerbot-dashboard.conf" "$nginx_conf"
}

# ---- nginx

nginx_target() {   # nginx_target <name>: where nginx reads that file
    if [ "$1" = minerbot-dashboard.conf ]; then
        printf '%s\n' "$nginx_conf"
    else
        printf '%s\n' "$nginx_secrets/$1"
    fi
}

# Writes the marker state/nginx-change, which holds the backup's folder, before any change, then copies each named file
# nginx reads into state/nginx-backup/<UTC>/ and lists the ones that didn't exist, so nginx_restore can put back exactly
# what was there. The file "complete" comes last: a marker whose backup isn't complete was cut off before any file
# changed. Keeps the newest 3 backups, and leaves the folder in nginx_backup_dir.
nginx_backup() {   # nginx_backup <name>...
    local name target old
    local -a all
    install -d -m 700 "$home/state" "$home/state/nginx-backup"
    nginx_backup_dir=$home/state/nginx-backup/$(date -u +%Y%m%dT%H%M%SZ)
    while [ -e "$nginx_backup_dir" ]; do
        sleep 1
        nginx_backup_dir=$home/state/nginx-backup/$(date -u +%Y%m%dT%H%M%SZ)
    done
    printf '%s\n' "$nginx_backup_dir" > "$home/state/nginx-change"
    install -d -m 700 "$nginx_backup_dir"
    : > "$nginx_backup_dir/missing"
    for name in "$@"; do
        target=$(nginx_target "$name")
        if [ -e "$target" ]; then
            cp -p "$target" "$nginx_backup_dir/$name"
        else
            printf '%s\n' "$name" >> "$nginx_backup_dir/missing"
        fi
    done
    : > "$nginx_backup_dir/complete"
    all=("$home"/state/nginx-backup/*/)
    if [ ${#all[@]} -gt 3 ]; then
        for old in "${all[@]:0:${#all[@]}-3}"; do
            rm -rf "${old%/}"
        done
    fi
}

# Puts back every file a backup kept, byte for byte (a copy beside it, then a rename), and removes the ones that didn't
# exist before. Files the backup doesn't name are left alone.
nginx_restore() {   # nginx_restore <backup folder>
    local dir=$1 name target tmp
    for name in "${nginx_names[@]}"; do
        target=$(nginx_target "$name")
        if [ -f "$dir/$name" ]; then
            install -d -m 700 "$nginx_secrets"
            tmp=$(mktemp "$(dirname "$target")/.minerbot-restore.XXXXXX")
            cp -p "$dir/$name" "$tmp"
            mv -f "$tmp" "$target"
        elif grep -qxF -- "$name" "$dir/missing" 2> /dev/null; then
            rm -f "$target"
        fi
    done
}

# Changes nginx's files for the dashboard, as deploy/README.md describes. Sets nginx_result to "reloaded" when the new
# files run and both smoke checks passed; "problem" when the new files run but a smoke check failed, with the words in
# nginx_problem; or "refused" when the standalone test, the full test or the reload failed, and the old files, back
# byte for byte, are what runs. INT, TERM and HUP are ignored from the first change until the marker is gone. A run
# killed in between leaves the marker, and the next setup run undoes the change from its backup first.
nginx_change() {   # nginx_change <folder with minerbot-dashboard.conf, origin-cert.pem, origin-key.pem, proxy-key.conf>
    local from=$1 staging=$nginx_secrets/.staging check=$home/state/nginx-check name
    nginx_result='' nginx_problem=''
    # 1. The marker and the backup, before any change
    nginx_backup "${nginx_names[@]}"
    trap '' INT TERM HUP
    # 2. The candidate on its own, with the server's main context, before it goes near conf.d
    install -d -m 700 "$nginx_secrets"
    rm -rf "$staging"
    install -d -m 700 "$staging"
    install -d -m 700 "$check"
    for name in origin-cert.pem origin-key.pem proxy-key.conf; do
        install -m 600 "$from/$name" "$staging/$name"
    done
    install -m 644 "$from/minerbot-dashboard.conf" "$staging/minerbot-dashboard.conf"
    sed -e "s#$nginx_secrets/#$staging/#g" -e '/pipelog/d' "$staging/minerbot-dashboard.conf" > "$check/candidate.conf"
    {
        main_context /etc/nginx/nginx.conf
        printf 'events {}\nhttp {\n    include %s;\n}\n' "$check/candidate.conf"
    } > "$check/nginx.conf"
    if ! run_for 30 nginx -t -q -c "$check/nginx.conf"; then
        rm -rf "$staging" "$nginx_backup_dir"
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        echo "nginx -t refused the new files on their own (above), before they went near nginx's folders. Nothing was changed."
        nginx_result=refused
        return 0
    fi
    # 3. Installed by rename, the conf.d file last. Both folders are under /etc, so each rename is atomic.
    for name in origin-cert.pem origin-key.pem proxy-key.conf; do
        mv -f "$staging/$name" "$nginx_secrets/$name"
    done
    mv -f "$staging/minerbot-dashboard.conf" "$nginx_conf"
    rmdir "$staging"
    # 4. With every site on this server
    if ! run_for 30 nginx -t -q; then
        nginx_put_back "nginx -t refused the new files (above), so the old ones are back and nginx wasn't reloaded."
        return 0
    fi
    # 5. cPanel's own reload
    if ! run_for 120 /scripts/restartsrv_nginx --reload; then
        nginx_put_back "cPanel's reload of nginx failed (above), so the old files are back, as nginx still runs them."
        return 0
    fi
    # 6. Done: nginx runs the new files
    rm -f "$home/state/nginx-change"
    trap - INT TERM HUP
    # 7. A request from this server isn't Cloudflare's, so it must get 403, and the certificate must be the new one
    nginx_smoke_check
}

nginx_put_back() {   # nginx_put_back <message>: every file back from the backup, and a second nginx -t, which must pass
    nginx_restore "$nginx_backup_dir"
    rm -rf "$nginx_secrets/.staging"
    if ! run_for 30 nginx -t -q; then
        echo "WARNING: nginx -t still fails with the old files back (above). Look at it now: until it passes, every reload of nginx fails, AutoSSL's included."
    fi
    rm -f "$home/state/nginx-change"
    trap - INT TERM HUP
    echo "$1"
    nginx_result=refused
}

nginx_smoke_check() {   # sets nginx_result to reloaded or problem
    local answer=000 served='' expected tries=0 pause=2
    if test_fast; then
        pause=0.2
    fi
    expected=$(run_for 10 openssl x509 -noout -fingerprint -sha256 -in "$nginx_secrets/origin-cert.pem" 2> /dev/null) || expected=
    while [ "$tries" -lt 5 ]; do
        tries=$((tries + 1))
        answer=$(local_answer)
        served=$(served_fingerprint 2> /dev/null) || served=
        if [ "$answer" = 403 ] && [ -n "$expected" ] && [ "$served" = "$expected" ]; then
            echo "nginx reloaded with the new files: a request from this server gets 403, and nginx serves the installed certificate."
            nginx_result=reloaded
            return 0
        fi
        sleep "$pause"
    done
    nginx_problem="nginx runs the new file, but it answered $answer to a request from this server, where 403 was expected"
    if [ -z "$expected" ] || [ "$served" != "$expected" ]; then
        nginx_problem+=", and the certificate it serves for $public_host isn't the installed one"
    fi
    nginx_problem+=". The other sites are unaffected, since nginx -t and the reload passed. Look for another block naming the host: grep -rn --exclude-dir=minerbot $public_host /etc/nginx"
    echo "$nginx_problem"
    nginx_result=problem
}

# Undoes an nginx change that was cut off, from its backup, byte for byte, then tests and reloads. With --no-reload it
# only puts the files back, for --remove-nginx, which tests itself. Sets nginx_result to none (there was no marker),
# undone, or failed (with the reason above).
nginx_undo_cut_off() {   # nginx_undo_cut_off [--no-reload]
    local dir=
    nginx_result=none
    [ -f "$home/state/nginx-change" ] || return 0
    IFS= read -r -t 5 dir < "$home/state/nginx-change" || true
    rm -rf "$nginx_secrets/.staging"
    if [[ $dir != "$home/state/nginx-backup/"* ]] || [ ! -f "$dir/complete" ]; then
        rm -f "$home/state/nginx-change"
        echo "An nginx change was cut off before it changed any file, so there was nothing to undo."
        nginx_result=undone
        return 0
    fi
    trap '' INT TERM HUP
    nginx_restore "$dir"
    if [ "${1:-}" = --no-reload ]; then
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        echo "An nginx change was cut off earlier. Its files are back as they were before it (${dir#"$home"/})."
        nginx_result=undone
        return 0
    fi
    if ! run_for 30 nginx -t -q; then
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        echo "An nginx change was cut off earlier. Its files are back as they were before it, but nginx -t fails with them (above), so nginx wasn't reloaded. Fix that first."
        nginx_result=failed
        return 0
    fi
    if ! run_for 120 /scripts/restartsrv_nginx --reload; then
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        echo "An nginx change was cut off earlier. Its files are back as they were before it and nginx -t passes, but cPanel's reload failed (above). Run /scripts/restartsrv_nginx --reload, then this again."
        nginx_result=failed
        return 0
    fi
    rm -f "$home/state/nginx-change"
    trap - INT TERM HUP
    echo "An nginx change was cut off earlier. Its files are back as they were before it, and nginx reloaded."
    nginx_result=undone
}

# The emergency path, for when nginx -t fails and names MinerBot's file: takes the file out of nginx, keeping a copy, then
# tests and reloads. It works while nginx -t fails, and changes nothing else. Sets nginx_result to absent, removed,
# reload-failed (out, and the test passes without it, but the reload failed) or other-file (the test still fails, so
# the file is back as it was and nothing was reloaded).
nginx_remove() {
    nginx_undo_cut_off --no-reload
    if [ ! -e "$nginx_conf" ]; then
        echo "MinerBot's file isn't in nginx. Nothing was changed."
        nginx_result=absent
        return 0
    fi
    nginx_backup minerbot-dashboard.conf
    trap '' INT TERM HUP
    rm -f "$nginx_conf"
    if ! run_for 30 nginx -t -q; then
        nginx_restore "$nginx_backup_dir"
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        echo "nginx -t still fails without MinerBot's file, so the problem is in another file (nginx's message is above). MinerBot's file was put back as it was, and nginx wasn't reloaded."
        nginx_result=other-file
        return 0
    fi
    if ! run_for 120 /scripts/restartsrv_nginx --reload; then
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        echo "MinerBot's file is out of nginx, and nginx -t passes without it, but cPanel's reload failed (above). nginx's next reload, AutoSSL's included, loads the configuration without it. Its copy: ${nginx_backup_dir#"$home"/}/"
        nginx_result=reload-failed
        return 0
    fi
    rm -f "$home/state/nginx-change"
    trap - INT TERM HUP
    echo "MinerBot's file is out of nginx, and nginx reloaded. Its copy: ${nginx_backup_dir#"$home"/}/"
    nginx_result=removed
}

# nginx.conf's main-context user, load_module and include lines, in their order: the standalone test copies them, since
# without the user line nginx falls back to its compiled-in user and fails wherever that user doesn't exist. An include
# with a relative path gets nginx.conf's folder in front, where nginx itself would look for it.
main_context() {   # main_context [<nginx.conf>, /etc/nginx/nginx.conf by default]
    local conf=${1:-/etc/nginx/nginx.conf}
    awk -v dir="$(dirname "$conf")" '
        depth == 0 && /^[ \t]*(user|load_module|include)[ \t]/ {
            if ($1 == "include" && $2 !~ /^\//) { sub(/include[ \t]+/, "include " dir "/") }
            print
        }
        { line = $0; sub(/#.*/, "", line); depth += gsub(/\{/, "{", line) - gsub(/\}/, "}", line) }' "$conf"
}

# The http-level look: every line of nginx's whole configuration outside server and types blocks, with its file. It never
# prints a secret: nginx -T runs as root and prints every included file, so it skips every file that isn't
# world-readable (under /etc/nginx, and any other file nginx -T names), the folder /etc/nginx/minerbot, and any line
# naming X-MinerBot-Proxy-Key or cPanel-localhost, whatever the letter case.
nginx_look() {
    local conf private file
    conf=$(run_for 30 nginx -T 2> /dev/null) || {
        echo "nginx -T failed. nginx -t shows why." >&2
        return 1
    }
    private=$(find -L /etc/nginx -type f ! -perm -o=r 2> /dev/null) || true
    while IFS= read -r -t 5 file; do
        if [ -n "$(find -L "$file" -maxdepth 0 -type f ! -perm -o=r 2> /dev/null)" ]; then
            private+=$'\n'$file
        fi
    done < <(printf '%s\n' "$conf" | sed -n 's/^# configuration file \(.*\):$/\1/p')
    {
        printf '%s\n' "$private"
        printf '\001\n'
        printf '%s\n' "$conf"
    } | awk '
        !listed { if ($0 == "\001") listed = 1; else if ($0 != "") private[$0] = 1; next }
        /^# configuration file / { file = $4; sub(/:$/, "", file); depth = 0; skip = 0
                                   secret = (file in private) || (file ~ /^\/etc\/nginx\/minerbot\//); next }
        secret { next }
        { line = $0; sub(/#.*/, "", line) }
        !skip && line ~ /^[ \t]*(server|types)[ \t]*(\{|$)/ { skip = 1; skip_at = depth; opened = 0 }
        { o = gsub(/\{/, "{", line); c = gsub(/\}/, "}", line); depth += o - c }
        skip { if (o) opened = 1; if (opened && depth <= skip_at) skip = 0; next }
        line ~ /[^ \t]/ && tolower($0) !~ /x-minerbot-proxy-key|cpanel-localhost/ { print file ": " $0 }'
}

# The SHA-256 fingerprint of the certificate nginx on this server serves for the dashboard's name
served_fingerprint() {
    run_for 10 openssl s_client -connect 127.0.0.1:443 -servername "$public_host" < /dev/null 2> /dev/null \
        | run_for 10 openssl x509 -noout -fingerprint -sha256
}

# The status nginx on this server gives the dashboard's name for a request from this server, which isn't Cloudflare's,
# so it must be 403 once the file is live; 000 when nothing answered
local_answer() {
    local code
    code=$(run_for 15 curl -sk --max-time 10 --resolve "$public_host:443:127.0.0.1" -o /dev/null -w '%{http_code}' \
        "https://$public_host/" 2> /dev/null) || true
    printf '%s\n' "${code:-000}"
}

# ---- Setup

on_terminal() {   # true when the input is a terminal; every stty is behind it
    [ -t 0 ]
}

# Throws away whatever a paste left in the terminal, such as the blank line a CRLF paste leaves after its END line, so the
# next question doesn't take it for an answer
drain_input() {
    local _
    on_terminal || return 0
    while IFS= read -r -s -t 0.3 _; do
        :
    done
}

# Reads one pasted PEM block into a file, line by line, each line waiting at most 10 minutes. Blank lines are skipped,
# since a CRLF paste arrives with a blank line after every line. The first line must be the BEGIN line, or "keep", and the
# paste ends by itself at the END line, at most 200 lines. hidden turns the terminal's echo off for the whole paste.
# Returns 0 for a block, 3 for keep, 4 when it's too long, 5 when nothing came, 6 when the first line isn't BEGIN.
paste_pem() {   # paste_pem <CERTIFICATE|PRIVATE KEY> <file> [hidden]
    local kind=$1 out=$2 hidden=${3:-} line count=0 status=5 limit
    limit=$(read_limit)
    : > "$out"
    if [ "$hidden" = hidden ] && on_terminal; then stty -echo; fi
    while :; do
        line=
        if [ "$hidden" = hidden ]; then
            IFS= read -r -s -t "$limit" line || { status=5; break; }
        else
            IFS= read -r -t "$limit" line || { status=5; break; }
        fi
        line=${line%$'\r'}
        [ -n "$line" ] || continue
        if [ "$count" -eq 0 ]; then
            if [ "$line" = keep ]; then
                status=3
                break
            fi
            if [[ $line != "-----BEGIN "*"$kind-----" ]]; then
                status=6
                break
            fi
        fi
        printf '%s\n' "$line" >> "$out"
        count=$((count + 1))
        if [[ $line == "-----END "*"$kind-----" ]]; then
            status=0
            break
        fi
        if [ "$count" -ge 200 ]; then
            status=4
            break
        fi
    done
    if [ "$hidden" = hidden ] && on_terminal; then stty echo; fi
    drain_input
    return "$status"
}

# Asks one question and checks the answer, three tries at most. Enter keeps the current value, when there is one. A hidden
# question isn't shown as it's typed. The check function prints why it refuses, and may tidy the answer. The answer is
# left in $answer.
ask() {   # ask <question> <current value> <check function> [hidden]
    local question=$1 current=$2 check=$3 hidden=${4:-} limit prompt _
    limit=$(read_limit)
    prompt=$question
    if [ -n "$current" ] && [ "$hidden" = hidden ]; then
        prompt+=" [Enter keeps the current one]"
    elif [ -n "$current" ]; then
        prompt+=" [Enter keeps $current]"
    fi
    for _ in 1 2 3; do
        answer=
        if [ "$hidden" = hidden ]; then
            IFS= read -r -s -t "$limit" -p "$prompt: " answer || die "No answer came in time, or the input ended. Nothing was changed."
            echo
        else
            IFS= read -r -t "$limit" -p "$prompt: " answer || die "No answer came in time, or the input ended. Nothing was changed."
        fi
        answer=${answer%$'\r'}
        if [ -z "$answer" ]; then
            answer=$current
        fi
        if "$check" "$answer"; then
            return 0
        fi
    done
    die "Three answers in a row weren't right. Nothing was changed."
}

# ---- Certificate

cert_end() {   # cert_end <certificate file>: its end date, such as 2041-11-05 11:45:57Z
    local out
    out=$(run_for 10 openssl x509 -noout -enddate -dateopt iso_8601 -in "$1" 2> /dev/null) || return 1
    printf '%s\n' "${out#notAfter=}"
}

cert_days_left() {   # cert_days_left <certificate file>: whole days until it ends, negative once it has
    local end seconds
    end=$(cert_end "$1") || return 1
    seconds=$(date -u -d "$end" +%s) || return 1
    echo $(((seconds - $(date -u +%s)) / 86400))
}

# ---- Backups

newest_backup() {   # the path of the newest dump in /srv/backups/minerbot, or nothing
    local line
    line=$(find "$backups" -maxdepth 1 -type f -name 'minerbot-*.sql.gz' -printf '%T@ %p\n' 2> /dev/null \
        | sort -rn | awk 'NR == 1') || line=
    printf '%s\n' "${line#* }"
}

# What's wrong with the backups, one line each: none newer than 36 hours, the last run failed, or a run that started more
# than 2 hours ago and never wrote another line
backup_problems() {
    local newest hours last='' when epoch
    newest=$(newest_backup)
    if [ -z "$newest" ]; then
        echo "There's no backup of MinerBot's database in $backups yet."
    else
        hours=$((($(date +%s) - $(stat -c %Y "$newest")) / 3600))
        if [ "$hours" -ge 36 ]; then
            echo "The newest backup of MinerBot's database, ${newest##*/}, is $hours hours old."
        fi
    fi
    if [ -f "$backups/backup.log" ]; then
        last=$(tail -n 1 "$backups/backup.log") || last=
    fi
    case $last in
        *" failed "* | *" failed")
            echo "The last backup failed ($last). See $backups/backup.log; ./backup.sh manual shows the step."
            ;;
        *" started "*)
            when=${last%% *}
            epoch=$(date -u -d "$when" +%s 2> /dev/null) || epoch=0
            if [ $(($(date +%s) - epoch)) -ge 7200 ]; then
                echo "A backup started at $when and never finished: it was stopped from outside, or the server restarted. See $backups/backup.log."
            fi
            ;;
    esac
}

free_kib() {   # free_kib <path>: the space free there, in KiB
    local out
    out=$(df --output=avail -k "$1" | tail -n 1) || return 1
    out=${out// /}
    [[ $out =~ ^[0-9]+$ ]] || return 1
    printf '%s\n' "$out"
}

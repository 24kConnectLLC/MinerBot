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
# Where nginx's master writes its PID, as nginx.conf's pid line says on this server
nginx_pid_file=/var/run/nginx.pid
# cPanel loads its pipelog module only while WHM's piped logging is on: this file holds the module's load_module line,
# and cPanel deletes it when the setting goes off. Each time cPanel rebuilds nginx's global files, it writes or deletes
# that file, then runs every program in pipelog_hook's folder, then tests and reloads nginx. pipelog_hook is MinerBot's:
# it keeps pipelog.conf in nginx_secrets, which the server file includes, in step with that file.
pipelog_module=/etc/nginx/conf.d/modules/ngx_http_pipelog_module.conf
pipelog_hook=/etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog
backups=/srv/backups/minerbot
# The dashboard listens only on this socket, never on a TCP port, so no other account on the server can take its place.
# Its folder is 2750, owned by 1654 (the dashboard) and the group nobody (nginx's workers): only the dashboard can make
# a file there, and only processes in the group nobody, nginx's workers and cPanel's Apache among them, can reach it.
# /run is emptied at every boot, so systemd-tmpfiles makes the folder again from tmpfiles_conf, which comes from
# deploy/minerbot-dashboard.tmpfiles. compose.yml and nginx's file name the same paths.
socket_dir=/run/minerbot-dashboard
dashboard_socket=$socket_dir/dashboard.sock
tmpfiles_conf=/etc/tmpfiles.d/minerbot-dashboard.conf
# The usual order, /usr/local first, which is also where scripts-test.sh puts its stand-ins
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# ---- Limits

# Runs a command with a time limit: at the limit, timeout sends TERM, and KILL <grace> seconds later (10 by default).
# Every call to Docker, MariaDB, curl, git, OpenSSL, nginx, sendmail, systemctl, systemd-run, systemd-tmpfiles, pgrep, ps
# and cPanel's /scripts/restartsrv_nginx in these scripts goes through it.
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
    die "${0##*/} stopped while ${step:-starting} (line $2, exit code $1)."
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
# and BOT_READY_PATTERN, and checks that both databases exist. Its messages end with $unchanged when it's set:
# setup-dashboard.sh sets it once its unit has undone a cut-off nginx change.
load_server_conf() {
    local count status=0
    [ -f "$home/server.conf" ] \
        || die "$home/server.conf is missing. bootstrap.sh writes it; server.conf.example in the repository shows its lines. ${unchanged:-Nothing was changed.}"
    SITE_DB=$(conf_value SITE_DB)
    BOT_DB=$(conf_value BOT_DB)
    BOT_READY_PATTERN=$(conf_value BOT_READY_PATTERN)
    BOT_READY_PATTERN=${BOT_READY_PATTERN:-Gateway +Ready}
    [[ $SITE_DB =~ ^[A-Za-z0-9_]{1,64}$ ]] \
        || die "SITE_DB in $home/server.conf must be the website's database name: 1 to 64 letters, digits and _. ${unchanged:-Nothing was changed.}"
    [[ $BOT_DB =~ ^[A-Za-z0-9_]{1,64}$ ]] \
        || die "BOT_DB in $home/server.conf must be MinerBot's own database name: 1 to 64 letters, digits and _. ${unchanged:-Nothing was changed.}"
    grep -qE -- "$BOT_READY_PATTERN" /dev/null || status=$?
    if [ "$status" != 1 ] || [ ${#BOT_READY_PATTERN} -gt 200 ]; then
        die "BOT_READY_PATTERN in $home/server.conf must be a pattern grep -E accepts, at most 200 characters. ${unchanged:-Nothing was changed.}"
    fi
    count=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name IN ('$SITE_DB', '$BOT_DB')") \
        || die "MariaDB didn't answer as root within 30 seconds (above). ${unchanged:-Nothing was changed.}"
    [ "$count" = 2 ] \
        || die "SITE_DB and BOT_DB in $home/server.conf must name two databases that exist on this server. Check both names. ${unchanged:-Nothing was changed.}"
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

# Whether a unit runs, by systemctl is-active within 10 seconds: 0 while it runs, and while systemd is still stopping
# it; 1 once systemd says it doesn't; 2 when systemctl gave no answer, as when it hits its limit under heavy load, which
# says nothing either way. A caller that only asks whether it runs takes 2 as no.
unit_running() {   # unit_running <unit>
    local state
    state=$(run_for 10 systemctl is-active "$1" 2> /dev/null) || true
    case $state in
        active | activating | deactivating | reloading | refreshing) return 0 ;;
        '') return 2 ;;
    esac
    return 1
}

# Shows the run's log as it grows until the unit ends, then returns 0 when its last RESULT line is "RESULT ok". Ctrl-C
# stops only the following: the run goes on. It looks every 2 seconds, or every 0.2 with MINERBOT_TEST_FAST=1. Before
# the limit, only systemd's answer that the unit doesn't run ends the wait, or the run's RESULT line while systemctl
# gives no answer: a systemctl that answered nothing says nothing about the run. Where tail can't be told about changes
# (no inotify, as on overlayfs), it reads the log every half second, or tenth with MINERBOT_TEST_FAST=1, and gets a
# second, or 0.3, after the unit ends to show the last lines.
follow() {   # follow <unit> <log> <limit seconds>
    local unit=$1 log=$2 limit=$3 started=$SECONDS result poll=2 tail_poll=0.5 settle=1 state=0
    if test_fast; then
        poll=0.2 tail_poll=0.1 settle=0.3
    fi
    tail -s "$tail_poll" -n +1 -f "$log" &
    follow_tail=$!
    follow_note="The ${unit#minerbot-} goes on. Follow it again with: tail -f $log"
    trap 'follow_interrupted' INT TERM HUP
    while [ $((SECONDS - started)) -lt "$limit" ]; do
        state=0
        unit_running "$unit" || state=$?
        if [ "$state" = 1 ] || { [ "$state" = 2 ] && grep -qE '^RESULT (ok|failed)$' "$log"; }; then
            break
        fi
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
    # No result: systemd said the unit doesn't run, or the limit passed
    if [ "$state" != 1 ]; then
        state=0
        unit_running "$unit" || state=$?
    fi
    case $state in
        0) echo "It still runs after $limit seconds, longer than its own limit. Look at it with: systemctl status $unit" ;;
        2) echo "It has no result after $limit seconds, longer than its own limit, and systemctl doesn't answer whether it still runs. Look at it with: systemctl status $unit" ;;
        *) echo "It ended without a result: it was stopped at its time limit, or the server restarted. See the log: $log" ;;
    esac
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

# Whether a container runs: true or false, or nothing when there's no such container. Any other failure stops the script,
# as in container_image: a wrong answer here would leave a stopped dashboard stopped. A container Docker is restarting
# counts as running.
container_running() {   # container_running <container>
    local out status=0
    out=$(run_for 30 docker container inspect -f '{{.State.Running}}' "$1" 2>&1) || status=$?
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
# and history only, so it never stops anything. The template asks for the label only when the image has labels and this
# one among them: Docker prints "<no value>" for a missing label asked for with index, and fails on an image without
# labels, which would read as no image at all.
revision() {   # revision <image or ID>
    local out id='' label=''
    if [ -z "$1" ]; then
        echo none
        return 0
    fi
    out=$(run_for 30 docker image inspect \
        -f '{{.Id}} {{with index .Config "Labels"}}{{with index . "org.opencontainers.image.revision"}}{{.}}{{end}}{{end}}' \
        "$1" 2> /dev/null) || out=
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
    local id
    id=$(image_id "$1")
    [ -n "$id" ] || return 0
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

# Keeps a copy of the installed compose.yml as state/compose-ready-<service>.yml, the compose.yml the service was last
# ready under. A swap that fails after compose.yml changed that service's settings puts it back (deploy.sh's swap), so
# the image before runs as it did. It only warns when it can't: nothing depends on it until such a failure.
note_ready() {   # note_ready bot|dashboard
    local tmp=''
    if install -d -m 700 "$home/state" && tmp=$(mktemp "$home/state/.compose-ready.XXXXXX") \
        && cp "$home/compose.yml" "$tmp" && mv -f "$tmp" "$home/state/compose-ready-$1.yml"; then
        return 0
    fi
    if [ -n "$tmp" ]; then
        rm -f "$tmp"
    fi
    echo "Warning: couldn't keep a copy of the compose.yml the $1 is ready under (above). Only a failed swap needs it."
}

# True when the installed compose.yml gives the service other settings than the compose.yml it was last ready under, by
# Compose's own config hash; false when they're the same, when there's no copy yet, or when Compose can't tell
compose_changed() {   # compose_changed bot|dashboard
    local kept=$home/state/compose-ready-$1.yml now was
    [ -f "$kept" ] || return 1
    now=$(run_for 30 docker compose -f "$home/compose.yml" --project-directory "$home" config --hash "$1" 2> /dev/null) \
        || return 1
    was=$(run_for 30 docker compose -f "$kept" --project-directory "$home" config --hash "$1" 2> /dev/null) || return 1
    [ -n "$now" ] && [ "$now" != "$was" ]
}

# ready_service bot|dashboard [<public status before the swap>] [<restart count it starts from>] [<image ID it must run>]
ready_service() {
    if [ "$1" = bot ]; then
        ready_bot "${3:-0}" "${4:-}"
    else
        ready_dashboard "${2:-}" "${3:-0}" "${4:-}"
    fi
}

# What's wrong with the dashboard's socket, as one clause, or nothing when nginx can use it: its folder is 2750, owned
# by 1654 and the group nobody, and the socket in it is a socket owned by 1654 and the group nobody, which the group can
# write to. Only root and 1654 can make a file in that folder, so a socket that passes is the dashboard's own.
socket_problem() {
    local folder uid='' group='' mode='' kind=''
    folder=$(stat -c '%a %u %G %F' "$socket_dir" 2> /dev/null) || folder=
    if [ "$folder" != "2750 1654 nobody directory" ]; then
        echo "its socket's folder $socket_dir is ${folder:-missing}, not a folder with mode 2750, owner 1654 and group nobody, which systemd-tmpfiles --create $tmpfiles_conf makes"
        return 0
    fi
    read -r -t 5 uid group mode kind <<< "$(stat -c '%u %G %a %F' "$dashboard_socket" 2> /dev/null)" || true
    if [ -z "$kind" ]; then
        echo "its socket $dashboard_socket isn't there"
    elif [ "$kind" != socket ] || [ "$uid" != 1654 ] || [ "$group" != nobody ]; then
        echo "$dashboard_socket isn't a socket owned by 1654 and the group nobody: it's a $kind of $uid and $group"
    elif [ $((8#$mode & 8#020)) = 0 ]; then
        echo "its socket's mode is $mode, so nginx, in the group nobody, can't connect to it"
    fi
}

# Waits until the dashboard is ready, for 90 seconds: its container runs the image it must, when one is given, with no
# new restart, and isn't unhealthy; socket_problem finds nothing; and /health/ready, asked through the socket and read
# up to 64 bytes, answers Healthy, which proves both databases with its own login and rights. Then, when the public home
# page answered 200 before the swap, it must answer 200 again within 30 seconds. A container on another image fails at
# once: Compose has returned, so nothing changes it any more.
ready_dashboard() {   # ready_dashboard [<public status before the swap>] [<restart count it starts from, 0 by default>] [<image ID it must run>]
    local public_before=${1:-} base=${2:-0} expected=${3:-} limit=90 public_limit=30 poll=2 started=$SECONDS
    local state image running restarts health said why
    if test_fast; then
        limit=10 public_limit=4 poll=0.5
    fi
    while :; do
        why=
        state=$(run_for 30 docker container inspect \
            -f '{{.Image}} {{.State.Running}} {{.RestartCount}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
            "$dashboard_container" 2> /dev/null) || state=
        image='' running='' restarts='' health=''
        read -r -t 5 image running restarts health <<< "$state" || true
        if [ -z "$state" ]; then
            why="its container doesn't exist"
        elif [ -n "$expected" ] && [ "$image" != "$expected" ]; then
            echo "The dashboard isn't ready: its container runs $(revision "$image"), not $(revision "$expected"), so Compose didn't recreate it."
            return 1
        elif [ "$restarts" != "$base" ]; then
            echo "The dashboard isn't ready: its container restarted (restart count $restarts)."
            return 1
        elif [ "$health" = unhealthy ]; then
            echo "The dashboard isn't ready: Docker's health check says unhealthy."
            return 1
        elif [ "$running" != true ]; then
            why="its container isn't running"
        else
            why=$(socket_problem)
            if [ -z "$why" ]; then
                said=$(run_for 10 curl -s --max-time 5 --unix-socket "$dashboard_socket" -H 'Host: localhost' \
                    http://localhost/health/ready 2> /dev/null | head -c 64) || true
                if [ "$said" = Healthy ]; then
                    break
                fi
                why="/health/ready answers \"${said:0:40}\""
            fi
        fi
        if [ $((SECONDS - started)) -ge "$limit" ]; then
            echo "The dashboard wasn't ready within $limit seconds: $why."
            return 1
        fi
        sleep "$poll"
    done
    echo "The dashboard is ready: /health/ready answers Healthy through its socket, which only it can make."
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

# Waits until the bot is up. Once its image has a health check, that means healthy. Until then, which is until PR 6
# gives it a heartbeat, it means: its log since it started has a line matching BOT_READY_PATTERN from server.conf within
# 150 seconds, and 20 seconds later it still runs with the same start time and no new restart. The log goes to a file
# only root can open, never to the screen, because it can hold a Steam sign-in code. As for the dashboard, a container on
# another image than the one given fails at once.
ready_bot() {   # ready_bot [<restart count it starts from, 0 by default>] [<image ID it must run>]
    local base=${1:-0} expected=${2:-} limit=150 steady=20 poll=3 started=$SECONDS pattern=${BOT_READY_PATTERN:-Gateway +Ready}
    local state image running restarts since health log count why started_again
    if test_fast; then
        limit=10 steady=1 poll=0.5
    fi
    install -d -m 700 "$home/state" "$home/state/tmp"
    log=$(mktemp "$home/state/tmp/bot-log.XXXXXX") || return 1
    while :; do
        why=
        state=$(run_for 30 docker container inspect \
            -f '{{.Image}} {{.State.Running}} {{.RestartCount}} {{.State.StartedAt}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
            "$bot_container" 2> /dev/null) || state=
        image='' running='' restarts='' since='' health=''
        read -r -t 5 image running restarts since health <<< "$state" || true
        if [ -z "$state" ]; then
            why="its container doesn't exist"
        elif [ -n "$expected" ] && [ "$image" != "$expected" ]; then
            echo "The bot isn't ready: its container runs $(revision "$image"), not $(revision "$expected"), so Compose didn't recreate it."
            rm -f "$log"
            return 1
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
# nothing. Returns 0 when exactly one line says the bot's container is Running and no line plans a change; 1 when a line
# plans a change; 2 when the dry run itself failed or didn't finish within 60 seconds, with its exit code in
# $dry_run_status (124 or 137 at the limit); 3 when its lines are none of these, from a newer Compose say. Compose's
# lines are left in $dry_run for the message.
bot_unchanged() {   # bot_unchanged <compose file>
    local line words running=0
    local -a parts
    dry_run_status=0
    dry_run=$(run_for 60 docker compose -f "$1" --project-directory "$home" up --dry-run -d --no-build --no-deps bot 2>&1) \
        || dry_run_status=$?
    [ "$dry_run_status" = 0 ] || return 2
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
    [ "$running" = 1 ] || return 3
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
# keys volume isn't touched. It gets the socket compose.yml gives the dashboard too, so it checks what will run.
check_settings() {   # check_settings <env file> <image>
    local status=0
    run_for 30 docker rm -f -v minerbot-dashboard-check > /dev/null 2>&1 || true
    run_for 60 docker run --rm --pull never --name minerbot-dashboard-check --network none --read-only \
        --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --tmpfs /app/keys:rw,noexec,nosuid,nodev,size=1m,mode=1777 \
        --cap-drop ALL --security-opt no-new-privileges:true --memory 256m --pids-limit 64 \
        --env-file "$1" -e "Dashboard__UnixSocket=$dashboard_socket" "$2" check-settings || status=$?
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

# What MinerBot's database holds that no migration makes, one per line and sorted, such as "trigger audit_log_stamp":
# its triggers, events and stored routines. A trigger or an event runs with the rights of the login that made it, so one
# made with the bot's own login, which has every right in that database and which the cPanel account owns, could change
# or delete what the dashboard's login writes there, the audit log included, and survive every rights apply. A pull
# request whose migration makes one changes this check.
bot_db_extras() {
    run_for 30 mariadb -N -e "SELECT CONCAT('trigger ', TRIGGER_NAME) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = '$BOT_DB' UNION ALL SELECT CONCAT('event ', EVENT_NAME) FROM information_schema.EVENTS WHERE EVENT_SCHEMA = '$BOT_DB' UNION ALL SELECT CONCAT(LOWER(ROUTINE_TYPE), ' ', ROUTINE_NAME) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = '$BOT_DB'" \
        | LC_ALL=C sort
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
# someone added a right REVOKE can't take away, such as a PROXY grant, which a person must remove. Once the comparison
# passes, it keeps a copy of the file it applied, as state/dashboard-rights.applied.sql (600). setup-dashboard.sh applies
# that copy, so a setup run never gives the running dashboard the rights of a newer commit than its last deploy applied.
# It refuses first, with the login untouched, while MinerBot's database holds anything bot_db_extras lists.
apply_rights() {   # apply_rights [<rights file>, the installed dashboard-rights.sql by default]
    local file=${1:-$home/dashboard-rights.sql} rehearsal real expected actual host copy='' extras
    rehearsal=$(render "$file" "'$check_login'@'localhost'") || return 1
    real=$(render "$file" "$grantees") || return 1
    if ! extras=$(bot_db_extras); then
        echo "Couldn't list the triggers, events and routines in MinerBot's database (above). The dashboard's login wasn't touched."
        return 1
    fi
    if [ -n "$extras" ]; then
        echo "MinerBot's database, $BOT_DB, holds what no migration makes: ${extras//$'\n'/, }. Each runs with the rights of the login that made it, on rows the dashboard's login writes, the audit log's included, and no rights file takes that away. The bot's own database login can make one, and so can the cPanel account that owns that login. Look at each one, as in SHOW CREATE TRIGGER \`$BOT_DB\`.\`<name>\`; drop it unless someone here made it on purpose, and find out who made it. The dashboard's login wasn't touched."
        return 1
    fi
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
    if ! install -d -m 700 "$home/state" || ! copy=$(mktemp "$home/state/.dashboard-rights.XXXXXX") \
        || ! cp -- "$file" "$copy" || ! mv -f -- "$copy" "$home/state/dashboard-rights.applied.sql"; then
        if [ -n "$copy" ]; then rm -f -- "$copy"; fi
        echo "The rights were applied and compared, but the copy setup-dashboard.sh applies couldn't be saved as state/dashboard-rights.applied.sql (above). Running this again saves it."
        return 1
    fi
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
        "minerbot.cron /etc/cron.d/minerbot 644" \
        "minerbot-dashboard.tmpfiles $tmpfiles_conf 644"
}

# The checks a commit's deploy files pass before any of them is installed: every file is there; bash -n passes on every
# script; every script but common.sh, which only holds functions, has the lines set -Eeuo pipefail and umask 077; no
# script turns tracing on, which would print secrets; Compose accepts the compose file; and the rights file fills in.
check_deploy_files() {   # check_deploy_files <folder> <commit>
    local stage=$1 name out
    local -a problems=()
    for name in compose.yml common.sh dashboard-rights.sql rights-list.sql minerbot-dashboard.conf deploy.sh rollback.sh \
        setup-dashboard.sh fill-secrets.sh backup.sh status.sh bootstrap.sh README.txt minerbot.cron \
        minerbot-dashboard.tmpfiles; do
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
# The installed files stay only when they're from a newer commit than this one, or from one on another line of main.
# When their commit is gone from src or no longer on main, main's history was rewritten, and this commit's files, which
# passed the check against main, replace them. Then the dashboard's socket folder is made, or put right, either way.
install_deploy_files() {   # install_deploy_files <commit> [<folder that holds that commit's deploy files>]
    local commit=$1 stage=${2:-} installed='' status=0 name target mode from tmp readme
    installed_files=()
    reexec_due=no
    if [ -f "$home/state/deploy-files.commit" ]; then
        IFS= read -r -t 5 installed < "$home/state/deploy-files.commit" || true
    fi
    if [ -n "$installed" ] && [ "$installed" != "$commit" ]; then
        run_for 10 git -C "$home/src" rev-parse --verify --quiet "$installed^{commit}" > /dev/null || status=$?
        if [ "$status" = 1 ]; then
            echo "The installed deploy files are from ${installed:0:12}, which src doesn't have any more: main's history was rewritten. The deploy files of ${commit:0:12} replace them."
        elif [ "$status" != 0 ]; then
            die "git couldn't tell whether src has ${installed:0:12}, the commit of the installed deploy files (above). Nothing was installed."
        elif deploy_files_before "$installed" "$commit"; then
            :
        elif deploy_files_before "$commit" "$installed"; then
            echo "Kept the deploy files of ${installed:0:12}, which are newer than ${commit:0:12}."
            make_socket_folder
            return 0
        elif deploy_files_before "$installed" refs/remotes/origin/main; then
            echo "Kept the deploy files of ${installed:0:12}, which is on main, on another line than ${commit:0:12}."
            make_socket_folder
            return 0
        else
            echo "The installed deploy files are from ${installed:0:12}, which isn't on main any more: main's history was rewritten. The deploy files of ${commit:0:12} replace them."
        fi
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
    make_socket_folder
}

# True when the first commit is an ancestor of the second commit or ref in src, false when it isn't; stops the script,
# with nothing installed, when git can't tell
deploy_files_before() {   # deploy_files_before <commit> <commit or ref>
    local status=0 later=$2
    run_for 10 git -C "$home/src" merge-base --is-ancestor "$1" "$2" || status=$?
    case $status in
        0 | 1) return "$status" ;;
    esac
    [[ $later == refs/* ]] || later=${later:0:12}
    die "git couldn't tell whether ${1:0:12} comes before $later (above). Nothing was installed."
}

# The dashboard's socket folder, as the installed tmpfiles file describes it and the boot makes it: systemd-tmpfiles
# makes it, or puts its mode and owners right, and never touches what's in it
make_socket_folder() {
    [ -f "$tmpfiles_conf" ] || return 0
    run_for 30 systemd-tmpfiles --create "$tmpfiles_conf" \
        || die "systemd-tmpfiles couldn't make $socket_dir, the folder of the dashboard's socket (above). Nothing was swapped. Run this again once that's fixed."
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

# True when nginx has MinerBot's file and the reference copy deploy.sh installed differs from it and is due: it's from
# the commit the dashboard runs, not waiting for its own dashboard (nginx_file_waits). deploy.sh's note uses it.
nginx_file_differs() {
    [ -f "$nginx_conf" ] && [ -f "$home/minerbot-dashboard.conf" ] && ! cmp -s "$home/minerbot-dashboard.conf" "$nginx_conf" \
        && ! nginx_file_waits
}

# True when the reference copy of nginx's file, which comes with the installed deploy files, is from another commit than
# the dashboard's image, as a --only bot or --no-start deploy, a stopped dashboard swap or a rollback leaves it. It then
# waits for the dashboard of its own commit: nginx's file moves forward only with the dashboard, so it never runs ahead of
# it. False when either commit is unknown. Leaves both in files_commit and dashboard_rev, for the messages.
nginx_file_waits() {
    files_commit='' dashboard_rev=''
    if [ -f "$home/state/deploy-files.commit" ]; then
        IFS= read -r -t 5 files_commit < "$home/state/deploy-files.commit" || true
    fi
    [[ $files_commit =~ ^[0-9a-f]{40}$ ]] || return 1
    dashboard_rev=$(dashboard_commit)
    [ -n "$dashboard_rev" ] && [ "$dashboard_rev" != "$files_commit" ]
}

# The full commit the dashboard's image was built from, by its revision label: the image its container runs, else
# minerbot-dashboard:current, else :new. Nothing when there's no such image or label, or Docker doesn't answer. It never
# stops anything, since deploy.sh asks at the very end of a deploy.
dashboard_commit() {
    local ref='' out='' id='' label='' template
    template='{{.Id}} {{with index .Config "Labels"}}{{with index . "org.opencontainers.image.revision"}}{{.}}{{end}}{{end}}'
    ref=$(run_for 30 docker container inspect -f '{{.Image}}' "$dashboard_container" 2> /dev/null) || ref=
    if [ -n "$ref" ]; then
        out=$(run_for 30 docker image inspect -f "$template" "$ref" 2> /dev/null) || out=
    else
        for ref in minerbot-dashboard:current minerbot-dashboard:new; do
            out=$(run_for 30 docker image inspect -f "$template" "$ref" 2> /dev/null) && break
            out=
        done
    fi
    read -r -t 5 id label <<< "$out" || true
    if [[ $label =~ ^[0-9a-f]{40}$ ]]; then
        printf '%s\n' "$label"
    fi
}

# ---- nginx

# proxy-key.conf's one line, which setup-dashboard.sh writes and status.sh compares: the header nginx adds to every
# request it passes to the dashboard
proxy_key_line() {   # proxy_key_line <key>
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$1"
}

nginx_target() {   # nginx_target <name>: where nginx reads that file
    if [ "$1" = minerbot-dashboard.conf ]; then
        printf '%s\n' "$nginx_conf"
    elif [ "$1" = minerbot-pipelog ]; then
        printf '%s\n' "$pipelog_hook"
    else
        printf '%s\n' "$nginx_secrets/$1"
    fi
}

# pipelog.conf's text, which the server file includes in both its blocks: "pipelog off;" while cPanel loads its pipelog
# module, so the dashboard's requests, Discord's sign-in codes among them, stay out of cPanel's piped domain logs;
# nothing while it doesn't, as when WHM's piped logging is off, since nginx would then refuse the directive for every site
pipelog_text() {
    if [ -e "$pipelog_module" ]; then
        printf 'pipelog off;\n'
    fi
}

# Writes pipelog_text to the file, by rename, unless the file holds it already
nginx_pipelog_conf() {   # nginx_pipelog_conf <file>
    local tmp
    tmp=$(mktemp "$(dirname "$1")/.minerbot-pipelog.XXXXXX")
    pipelog_text > "$tmp"
    if cmp -s "$tmp" "$1"; then
        rm -f "$tmp"
        return 0
    fi
    sync -- "$tmp"
    mv -f "$tmp" "$1"
}

# The program setup puts at pipelog_hook, where cPanel runs it each time it rebuilds nginx's global files: right after
# it writes or deletes the pipelog module's load_module line, and before its own nginx -t and reload. It writes
# pipelog.conf as pipelog_text does, so WHM's piped logging can go off or on without nginx refusing MinerBot's file, and
# every site with it. It does nothing once nginx_secrets is gone.
pipelog_hook_text() {
    printf '%s\n' '#!/bin/sh' \
        "# MinerBot's, from deploy/common.sh in the MinerBot repository; setup-dashboard.sh installs it. cPanel runs" \
        "# it each time it rebuilds nginx's global files, after it writes or deletes the pipelog module's load line" \
        "# and before it tests and reloads nginx. MinerBot's nginx file includes $nginx_secrets/pipelog.conf, which" \
        "# this keeps in step with that line: \"pipelog off;\" while the module loads, nothing while it doesn't," \
        "# since nginx would then refuse the directive for every site. It does nothing once that folder is gone." \
        "dir=$nginx_secrets" "module=$pipelog_module"
    cat << 'EOF'
[ -d "$dir" ] || exit 0
tmp=$(mktemp "$dir/.minerbot-pipelog.XXXXXX") || exit 1
if [ -e "$module" ]; then
    printf 'pipelog off;\n' > "$tmp" || { rm -f "$tmp"; exit 1; }
fi
if cmp -s "$tmp" "$dir/pipelog.conf"; then
    rm -f "$tmp"
    exit 0
fi
mv -f "$tmp" "$dir/pipelog.conf" || { rm -f "$tmp"; exit 1; }
EOF
}

# Writes the marker state/nginx-change, which holds the backup's folder, before any change, then copies each named file
# nginx reads into state/nginx-backup/<UTC>/ and lists the ones that didn't exist, so nginx_restore can put back exactly
# what was there. The copies are on disk before the file "complete", which comes last: a marker whose backup isn't
# complete was cut off before any file changed, and a power loss never leaves a complete backup of empty copies. Keeps
# the newest 3 backups, and leaves the folder in nginx_backup_dir.
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
    sync -- "$nginx_backup_dir"/* "$nginx_backup_dir"
    : > "$nginx_backup_dir/complete"
    all=("$home"/state/nginx-backup/*/)
    if [ ${#all[@]} -gt 3 ]; then
        for old in "${all[@]:0:${#all[@]}-3}"; do
            rm -rf "${old%/}"
        done
    fi
}

# Puts back every file a backup kept, byte for byte (a copy beside it, on disk, then a rename), and removes the ones that
# didn't exist before. Files the backup doesn't name are left alone. When the backup names the conf.d file, that file
# goes out first and comes back last, so a run cut off in between never leaves nginx a live conf.d file beside a
# certificate and key that don't belong together, which would fail nginx -t, and every reload and start, for every site.
# pipelog.conf follows cPanel's setting, which may have changed since the backup, as cPanel's hook would make it, before
# the conf.d file comes back: a "pipelog off;" while cPanel doesn't load its module would fail nginx -t the same way.
nginx_restore() {   # nginx_restore <backup folder>
    local dir=$1 name target tmp
    if [ -f "$dir/minerbot-dashboard.conf" ] || grep -qxF minerbot-dashboard.conf "$dir/missing" 2> /dev/null; then
        rm -f "$nginx_conf"
    fi
    for name in origin-cert.pem origin-key.pem proxy-key.conf pipelog.conf minerbot-pipelog minerbot-dashboard.conf; do
        target=$(nginx_target "$name")
        if [ -f "$dir/$name" ]; then
            # cPanel's folder of config scripts, gone since the backup, means cPanel no longer runs the hook
            [ "$name" != minerbot-pipelog ] || [ -d "$(dirname "$target")" ] || continue
            install -d -m 700 "$nginx_secrets"
            tmp=$(mktemp "$(dirname "$target")/.minerbot-restore.XXXXXX")
            cp -p "$dir/$name" "$tmp"
            sync -- "$tmp"
            mv -f "$tmp" "$target"
        elif grep -qxF -- "$name" "$dir/missing" 2> /dev/null; then
            rm -f "$target"
        fi
        if [ "$name" = pipelog.conf ] && [ -f "$target" ]; then
            nginx_pipelog_conf "$target"
        fi
    done
}

# Changes nginx's files for the dashboard, as deploy/README.md describes. With the four files it's given, it installs
# pipelog.conf and cPanel's hook, pipelog_hook, which keeps pipelog.conf in step with WHM's piped logging from then on.
# Sets nginx_result to "reloaded" when nginx took the reload of the new files and both smoke checks passed; "problem"
# when the new files run but a smoke check failed, with the words in nginx_problem; or "refused" when the standalone
# test or the full test failed, or nginx didn't take the reload, and the old files, back byte for byte, are what runs.
# After a reload nginx may have taken that holds only once a second reload, of the old files, was taken; when it isn't,
# the marker stays. INT, TERM and HUP are ignored from the first change until the marker is gone. A run killed in
# between leaves the marker, and the next setup run undoes the change from its backup first.
nginx_change() {   # nginx_change <folder with minerbot-dashboard.conf, origin-cert.pem, origin-key.pem, proxy-key.conf>
    local from=$1 staging=$nginx_secrets/.staging check=$home/state/nginx-check name reload=0
    nginx_result='' nginx_problem=''
    # 1. The marker and the backup, before any change
    nginx_backup "${nginx_names[@]}" pipelog.conf minerbot-pipelog
    trap '' INT TERM HUP
    # 2. The candidate on its own, with the server's main context, before it goes near conf.d
    install -d -m 700 "$nginx_secrets"
    rm -rf "$staging"
    install -d -m 700 "$staging"
    install -d -m 700 "$check"
    for name in origin-cert.pem origin-key.pem proxy-key.conf; do
        install -m 600 "$from/$name" "$staging/$name"
    done
    pipelog_text > "$staging/pipelog.conf"
    chmod 600 "$staging/pipelog.conf"
    pipelog_hook_text > "$staging/minerbot-pipelog"
    chmod 755 "$staging/minerbot-pipelog"
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
    # 3. Installed by rename. The staged files are on disk first, so a power loss never leaves an empty one in place. Our
    # conf.d file goes out first and comes back last, so a run cut off in between never leaves nginx a live conf.d file
    # beside a certificate and key that don't belong together, or beside files it names that aren't there yet: either
    # would fail nginx -t, and every reload and start, for every site. A cPanel reload in those few milliseconds loads
    # nginx without the dashboard, until step 5's. Both folders are under /etc, so each rename is atomic.
    sync -- "$staging"/* "$staging"
    rm -f "$nginx_conf"
    for name in origin-cert.pem origin-key.pem proxy-key.conf pipelog.conf; do
        mv -f "$staging/$name" "$nginx_secrets/$name"
    done
    if [ -d "${pipelog_hook%/*}" ]; then
        mv -f "$staging/minerbot-pipelog" "$pipelog_hook"
    else
        rm -f "$staging/minerbot-pipelog"
        echo "Note: cPanel's folder for nginx's config scripts, ${pipelog_hook%/*}, isn't on this server, so pipelog.conf can't follow WHM's piped logging. Keep piped logging on: with it off, nginx refuses MinerBot's file, and every site with it."
    fi
    mv -f "$staging/minerbot-dashboard.conf" "$nginx_conf"
    rmdir "$staging"
    # 4. With every site on this server
    if ! run_for 30 nginx -t -q; then
        nginx_put_back "nginx -t refused the new files (above), so the old ones are back and nginx wasn't reloaded."
        return 0
    fi
    # 5. cPanel's own reload, which nginx must take. One that failed may still have loaded the new files, and so may one
    # that nginx loads after its wait, so the old ones are loaded again either way.
    cpanel_reload || reload=$?
    if [ "$reload" = 1 ]; then
        nginx_put_back --reload "cPanel's reload of nginx failed (above), so the old files are back."
        return 0
    elif [ "$reload" = 2 ]; then
        nginx_put_back --reload "nginx didn't take cPanel's reload (above), so it still runs the old files, and they're back."
        return 0
    fi
    # 6. Done: nginx runs the new files
    rm -f "$home/state/nginx-change"
    trap - INT TERM HUP
    # 7. A request from this server isn't Cloudflare's, so it must get 403, and the certificate must be the new one
    nginx_smoke_check
}

# Every file back from the backup, then a second nginx -t, which must pass. With --reload, for a reload that nginx may
# have taken with the new files after all, nginx then loads the old ones again. The marker goes only once nginx took that
# reload: a run cut off before, or a second reload that fails or isn't taken, leaves it, so the next setup run puts the
# old files back and reloads first, and deploy.sh and status.sh --check report it until then.
nginx_put_back() {   # nginx_put_back [--reload] <message>
    local again=no said reload=0
    if [ "$1" = --reload ]; then
        again=yes
        shift
    fi
    said=$1
    nginx_restore "$nginx_backup_dir"
    rm -rf "$nginx_secrets/.staging"
    if ! run_for 30 nginx -t -q; then
        echo "WARNING: nginx -t still fails with the old files back (above). Look at it now: until it passes, every reload of nginx fails, AutoSSL's included."
    elif [ "$again" = yes ]; then
        cpanel_reload || reload=$?
        if [ "$reload" != 0 ]; then
            trap - INT TERM HUP
            echo "$said"
            if [ "$reload" = 1 ]; then
                echo "WARNING: cPanel's reload failed again, with the old files back (above). If the first reload took effect before it failed, nginx still runs the new files, which don't match dashboard.env. The marker state/nginx-change stays, so the next ./setup-dashboard.sh puts the old files back and reloads nginx first. Or run /scripts/restartsrv_nginx --reload once it works."
            else
                echo "WARNING: nginx didn't take the reload of the old files either (above), so it takes no reload now, AutoSSL's included, until what its error log names is fixed. It runs what it ran before this change, unless a reload took the new files after all. The marker state/nginx-change stays, so the next ./setup-dashboard.sh puts the old files back and reloads nginx first."
            fi
            nginx_result=refused
            return 0
        fi
        said+=" nginx was reloaded with them, in case the first reload had loaded the new ones after all."
    fi
    rm -f "$home/state/nginx-change"
    trap - INT TERM HUP
    echo "$said"
    nginx_result=refused
}

# After a reload nginx took: a request from this server, which isn't Cloudflare's, gets 403, and nginx serves the
# installed certificate. Neither sees the proxy key, which only the reload's new workers prove.
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
    nginx_problem="nginx runs the new file, but"
    if [ "$answer" != 403 ]; then
        nginx_problem+=" it answered $answer to a request from this server, where 403 was expected"
    fi
    if [ -z "$expected" ] || [ "$served" != "$expected" ]; then
        if [ "$answer" != 403 ]; then
            nginx_problem+=", and"
        fi
        nginx_problem+=" the certificate it serves for $public_host isn't the installed one"
    fi
    nginx_problem+=". The other sites are unaffected, since nginx -t and the reload passed. Look for another block naming the host: grep -rn --exclude-dir=minerbot $public_host /etc/nginx"
    echo "$nginx_problem"
    nginx_result=problem
}

# Undoes an nginx change that was cut off, from its backup, byte for byte, then tests and reloads. With --no-reload it
# only puts the files back, for --remove-nginx, which tests itself. Sets nginx_result to none (there was no marker, or
# its change was cut off before it changed any file), undone, or failed (with the reason above). The cut-off run's
# staging folder goes, and so does any copy a cut-off restore left beside its target, which may hold the old key.
nginx_undo_cut_off() {   # nginx_undo_cut_off [--no-reload]
    local dir='' reload=0
    nginx_result=none
    [ -f "$home/state/nginx-change" ] || return 0
    IFS= read -r -t 5 dir < "$home/state/nginx-change" || true
    rm -rf "$nginx_secrets/.staging"
    rm -f "$nginx_secrets"/.minerbot-restore.* "$nginx_secrets"/.minerbot-pipelog.* \
        "$(dirname "$nginx_conf")"/.minerbot-restore.* "${pipelog_hook%/*}"/.minerbot-restore.*
    if [[ $dir != "$home/state/nginx-backup/"* ]] || [ ! -f "$dir/complete" ]; then
        rm -f "$home/state/nginx-change"
        echo "An nginx change was cut off before it changed any file, so there was nothing to undo."
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
    cpanel_reload || reload=$?
    if [ "$reload" != 0 ]; then
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        if [ "$reload" = 1 ]; then
            echo "An nginx change was cut off earlier. Its files are back as they were before it and nginx -t passes, but cPanel's reload failed (above). Run /scripts/restartsrv_nginx --reload, then this again."
        else
            echo "An nginx change was cut off earlier. Its files are back as they were before it and nginx -t passes, but nginx didn't take cPanel's reload (above). Fix what its error log names, run /scripts/restartsrv_nginx --reload, then this again."
        fi
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
    local reload=0
    nginx_undo_cut_off --no-reload
    if [ ! -e "$nginx_conf" ]; then
        if [ "$nginx_result" = undone ]; then
            echo "MinerBot's file isn't in nginx once the cut-off change is undone (above), so nothing else was changed."
        else
            echo "MinerBot's file isn't in nginx. Nothing was changed."
        fi
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
    cpanel_reload || reload=$?
    if [ "$reload" != 0 ]; then
        rm -f "$home/state/nginx-change"
        trap - INT TERM HUP
        if [ "$reload" = 1 ]; then
            echo "MinerBot's file is out of nginx, and nginx -t passes without it, but cPanel's reload failed (above). nginx's next reload, AutoSSL's included, loads the configuration without it. Its copy: ${nginx_backup_dir#"$home"/}/"
        else
            echo "MinerBot's file is out of nginx, and nginx -t passes without it, but nginx didn't take cPanel's reload (above), so it still runs the file. The first reload nginx takes loads the configuration without it. Its copy: ${nginx_backup_dir#"$home"/}/"
        fi
        nginx_result='reload-failed'
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

# True when anyone may enter every folder above the file, its links followed: a world-readable file in a folder only
# root can enter is root's alone
world_reachable() {   # world_reachable <file>
    local dir
    dir=$(readlink -f -- "$1") && [ -n "$dir" ] || return 1
    while [ -n "$dir" ]; do
        dir=${dir%/*}
        [ -n "$(find "${dir:-/}" -maxdepth 0 -type d -perm -o=x 2> /dev/null)" ] || return 1
    done
}

# The http-level look: every line of nginx's whole configuration outside server and types blocks, with its file. It never
# prints a secret: nginx -T runs as root and prints every included file, so it prints only the files nginx -T names that
# anyone may read, world-readable in folders anyone may enter, by their whole path, spaces included; whatever it can't
# match to such a file, it leaves out. It also skips the folder /etc/nginx/minerbot whatever the modes there, and any
# line naming X-MinerBot-Proxy-Key or cPanel-localhost, whatever the letter case.
nginx_look() {
    local conf public='' file
    conf=$(run_for 30 nginx -T 2> /dev/null) || {
        echo "nginx -T failed. nginx -t shows why." >&2
        return 1
    }
    while IFS= read -r -t 5 file; do
        if [ -n "$(find -L "$file" -maxdepth 0 -type f -perm -o=r 2> /dev/null)" ] && world_reachable "$file"; then
            public+=$file$'\n'
        fi
    done < <(printf '%s\n' "$conf" | sed -n 's/^# configuration file \(.*\):$/\1/p')
    {
        printf '%s' "$public"
        printf '\001\n'
        printf '%s\n' "$conf"
    } | awk '
        !listed { if ($0 == "\001") listed = 1; else if ($0 != "") public[$0] = 1; next }
        /^# configuration file / { file = $0; sub(/^# configuration file /, "", file); sub(/:$/, "", file)
                                   depth = 0; skip = 0
                                   secret = !(file in public) || (file ~ /^\/etc\/nginx\/minerbot\//); next }
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

# The PIDs of nginx's worker processes, the master's children that nginx titles "nginx: worker process", one per line
# and sorted; nothing when the master can't be found or its workers can't be listed. Workers an earlier reload is
# letting finish, titled "nginx: worker process is shutting down", are among them.
nginx_worker_pids() {
    local master=''
    { IFS= read -r -t 5 master < "$nginx_pid_file"; } 2> /dev/null || true
    [[ $master =~ ^[0-9]+$ ]] || return 0
    run_for 10 pgrep -P "$master" -f '^nginx: worker process' 2> /dev/null | LC_ALL=C sort || true
}

# cPanel's reload of nginx, and the proof that nginx took it. The reload only signals nginx's master, and cPanel's script
# exits 0 whatever the master does then. A master that loads the new configuration starts new workers for it; one that
# can't, as when a new listen address is held by another program, which nginx -t never tries, keeps its old workers and
# its old configuration, and says why only in its error log. Neither the 403 check nor the certificate can tell those
# apart when only the proxy key changed. So the reload counts once a worker runs that didn't before, within 10 seconds.
# Returns 0 when one does, 1 when cPanel's reload failed, and 2, with the reason, when it exited 0 but nginx kept its old
# workers. When the workers can't be listed beforehand, cPanel's exit code is all there is, and it says so.
cpanel_reload() {
    local before after new limit=10 poll=0.5 started
    if test_fast; then
        limit=2 poll=0.2
    fi
    before=$(nginx_worker_pids)
    run_for 120 /scripts/restartsrv_nginx --reload || return 1
    if [ -z "$before" ]; then
        echo "Note: nginx's worker processes couldn't be listed from $nginx_pid_file, so only cPanel's exit code says nginx took the reload."
        return 0
    fi
    started=$SECONDS
    while :; do
        after=$(nginx_worker_pids)
        new=$(printf '%s\n' "$after" | grep -vxF -f <(printf '%s\n' "$before")) || new=
        if [ -n "$new" ]; then
            return 0
        fi
        if [ $((SECONDS - started)) -ge "$limit" ]; then
            echo "cPanel's reload exited 0, but nginx kept its old worker processes for $limit seconds, so it didn't load the files on disk: nginx keeps its running configuration when it can't apply a new one, as with a listen address another program holds, which nginx -t never tries, and says why only in its error log."
            echo "nginx's reason: grep -F '[emerg]' /var/log/nginx/error.log | tail -n 5"
            return 2
        fi
        sleep "$poll"
    done
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
# paste ends by itself at the END line, at most 200 lines. hidden turns the terminal's echo off for the whole paste and
# for the draining of what's left of it, so a key pasted along never shows.
# Returns 0 for a block, 3 for keep, 4 when it's too long, 5 when nothing came, 6 when the first line isn't BEGIN. With
# 6, pem_begin holds the label of a BEGIN line of another kind, such as PRIVATE KEY, which is no secret, or nothing.
paste_pem() {   # paste_pem <CERTIFICATE|PRIVATE KEY> <file> [hidden]
    local kind=$1 out=$2 hidden=${3:-} line count=0 status=5 limit
    pem_begin=
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
                if [[ $line =~ ^-----BEGIN\ ([A-Z0-9 ]{1,40})-----$ ]]; then
                    pem_begin=${BASH_REMATCH[1]}
                fi
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
    # Drained before echo comes back: each read -s puts back the state it found, so the whole drain runs without echo
    drain_input
    if [ "$hidden" = hidden ] && on_terminal; then stty echo; fi
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

#!/usr/bin/env bash
# Checks the logic of MinerBot's server scripts: deploy.sh, rollback.sh, setup-dashboard.sh, fill-secrets.sh, backup.sh,
# status.sh and bootstrap.sh. Nothing else can try them before the server, so this runs them for real in a throwaway
# AlmaLinux 9 container, the server's bash and coreutils, with a real /opt/minerbot and real git, flock, timeout,
# openssl, script and useradd. deploy/tests/fakes holds stand-ins for Docker, MariaDB, nginx, cPanel's reload, systemd,
# curl, sendmail, df and date, and thin wrappers around git, openssl and pgrep. Each stand-in records its calls in
# /fake/calls.log with the time limit it ran under, and FAKE_* variables make it fail where a case needs that. A local
# bare repository plays GitHub through git's url.<base>.insteadOf, so src's remote.origin.url still reads GitHub's
# address. Each case starts from a saved state of the whole server, so one failure doesn't hide the others.
#
# Run it from Git Bash or Linux: bash deploy/tests/scripts-test.sh. It needs Docker, and pulls almalinux:9 if it isn't
# there. It prints one line per case, then "N of <number of cases> passed", and exits 1 if any case failed. Case 13 ends
# with the one check that uses this machine's Docker directly: Compose's env_file and docker run --env-file must read the
# same values from the dashboard.env setup wrote.
#
# Everything it makes in Docker is named mbtest-scripts-<its PID>-... and removed when it ends, also when it's stopped
# with Ctrl-C, TERM or HUP: the cases run in the background, so the trap that removes the container runs at once. The
# container runs with --rm and its main process is sleep 1500, so even a run killed outright leaves nothing behind after
# 25 minutes: Docker removes the container when sleep ends. The cases inside it get 20 minutes at most.
#
# Every function named case_<number> is a case, and they run in number order, 19 and 20 last, because those two check
# what all the others left. A new case needs only its function. While working on a case: MBTEST_CASES="4 5" runs only
# those cases (the others count as not run), and MBTEST_SAVE=<folder> copies the container's outputs, logs and calls.log
# there before it's removed.
#
# Each check reads "<condition> && <condition> || problem ...": a problem is noted when any condition fails.
# shellcheck disable=SC2015
set -uo pipefail
umask 077
# Git Bash would otherwise turn the container paths below into Windows paths
export MSYS_NO_PATHCONV=1

image=almalinux:9

case_list() {   # every case's number, one per line, in the order they run: by number, then 19 and 20
    declare -F | awk '$3 ~ /^case_[0-9]+$/ { n = substr($3, 6); if (n != 19 && n != 20) print n }' | sort -n
    printf '%s\n' 19 20
}

# ======================================================================================================================
# The part that runs on this machine: the container, case 13's env-file check, case 20's Git modes, and the count
# ======================================================================================================================

host_path() {   # host_path <path>: the path as this machine's docker takes it, a Windows path in Git Bash
    if command -v cygpath > /dev/null 2>&1; then
        cygpath -m "$1"
    else
        printf '%s\n' "$1"
    fi
}

outside_cleanup() {
    local left=''
    if [ -f "$work/compose.yml" ]; then
        timeout -k 10 120 docker compose -p "$prefix-env" -f "$(host_path "$work/compose.yml")" down --remove-orphans \
            > /dev/null 2>&1 || true
    fi
    for _ in 1 2 3; do
        timeout -k 10 60 docker rm -f -v "$box" "$prefix-envrun" > /dev/null 2>&1 || true
        left=$( {
            timeout -k 10 30 docker ps -aq --filter "name=$prefix-"
            timeout -k 10 30 docker network ls -q --filter "name=$prefix-"
            timeout -k 10 30 docker volume ls -q --filter "name=$prefix-"
        } 2> /dev/null || true)
        if [ -z "$left" ]; then break; fi
        sleep 1
    done
    if [ -n "$left" ]; then
        echo "Some of this run's containers, networks or volumes are still there: look with docker ps -a --filter name=$prefix-"
    fi
    if [ -n "$work" ]; then rm -rf "$work" || true; fi
}

host_problem() {   # host_problem <what's wrong>
    host_notes+="    $1"$'\n'
}

# Case 13's last check: the dashboard.env setup wrote, read by Compose's env_file and by docker run --env-file. Both
# must give exactly the file's value for every key.
host_case_13() {
    local file=$work/dashboard.env name value from_compose from_run line
    if ! timeout -k 10 30 docker exec "$box" cat /out/case13-dashboard.env > "$file" 2> /dev/null || [ ! -s "$file" ]; then
        host_problem "setup didn't leave a dashboard.env to compare (the inner part of this case says why)"
        return 0
    fi
    printf '%s\n' "name: $prefix-env" "services:" "  env:" "    image: $image" "    network_mode: none" \
        "    env_file: ./dashboard.env" '    command: ["env"]' > "$work/compose.yml"
    if ! from_compose=$(timeout -k 10 180 docker compose -p "$prefix-env" -f "$(host_path "$work/compose.yml")" \
        run -T --rm --no-deps env 2> "$work/compose.err"); then
        host_problem "docker compose run failed: $(head -c 400 "$work/compose.err")"
        return 0
    fi
    if ! from_run=$(timeout -k 10 120 docker run --rm --name "$prefix-envrun" --network none \
        --env-file "$(host_path "$file")" "$image" env 2>&1); then
        host_problem "docker run --env-file failed: ${from_run:0:400}"
        return 0
    fi
    while IFS= read -r line || [ -n "$line" ]; do
        case $line in '#'* | '') continue ;; esac
        name=${line%%=*}
        value=${line#*=}
        if [ "$(printf '%s\n' "$from_compose" | tr -d '\r' | grep -m 1 "^$name=" | cut -d= -f2-)" != "$value" ]; then
            host_problem "Compose's env_file reads $name differently from the file"
        fi
        if [ "$(printf '%s\n' "$from_run" | tr -d '\r' | grep -m 1 "^$name=" | cut -d= -f2-)" != "$value" ]; then
            host_problem "docker run --env-file reads $name differently from the file"
        fi
    done < "$file"
}

# Case 20's Git modes: every script and stand-in is recorded as 100755. A file that isn't in Git yet is skipped and
# named; in CI every file is tracked, so all are checked.
host_case_20() {
    local tracked f rel mode
    local -a skipped=()
    if ! tracked=$(git -C "$(host_path "$repo")" ls-files -s -- deploy 2> /dev/null); then
        host_info+="    Not a Git checkout, so the files' modes in Git weren't checked."$'\n'
        return 0
    fi
    for f in "$repo"/deploy/*.sh "$repo"/deploy/tests/*.sh "$repo"/deploy/tests/fakes/*; do
        [ -f "$f" ] || continue
        rel=${f#"$repo"/}
        mode=$(printf '%s\n' "$tracked" | awk -v p="$rel" '{ split($0, a, "\t") } a[2] == p { print $1 }')
        if [ -z "$mode" ]; then
            skipped+=("$rel")
        elif [ "$mode" != 100755 ]; then
            host_problem "$rel is recorded in Git as $mode, not 100755: git add --chmod=+x $rel"
        fi
    done
    if [ ${#skipped[@]} -gt 0 ]; then
        host_info+="    Not in Git yet, so their modes weren't checked: ${skipped[*]}"$'\n'
    fi
}

finish_on_host() {   # finish_on_host <case number>: the verdict of a case the host finishes
    local n=$1 word message inner_notes
    host_notes=''
    host_info=''
    word=$(awk -v n="$n" '$1 == "pending" && $2 == n { print $3 }' "$work/results")
    if [ -z "$word" ]; then
        printf 'FAILED: case %s: it didn'"'"'t run, because the run stopped before it\n' "$n"
        return 0
    fi
    message=$(timeout -k 10 30 docker exec "$box" cat "/out/msg-$n" 2> /dev/null) || message="(no message)"
    inner_notes=$(timeout -k 10 30 docker exec "$box" cat "/out/notes-$n" 2> /dev/null) || inner_notes=''
    "host_case_$n"
    if [ "$word" = ok ] && [ -z "$host_notes" ]; then
        printf 'ok: case %s: %s\n' "$n" "$message"
        printf '%s' "$host_info"
        passed=$((passed + 1))
    else
        printf 'FAILED: case %s: %s\n' "$n" "$message"
        if [ -n "$inner_notes" ]; then printf '%s\n' "$inner_notes"; fi
        printf '%s%s' "$host_notes" "$host_info"
    fi
}

outside() {
    local here n all
    here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
    repo=$(cd "$here/../.." && pwd)
    prefix=mbtest-scripts-$$
    box=$prefix-box
    work=$(mktemp -d)
    passed=0
    all=$(case_list)
    cases=$(printf '%s\n' "$all" | grep -c .)
    trap outside_cleanup EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP

    echo "Starting $box from $image; the cases run inside it."
    if ! timeout -k 10 300 docker run -d --rm --init --name "$box" "$image" sleep 1500 > "$work/start.out" 2>&1; then
        echo "STOPPED: couldn't start the container: $(head -c 600 "$work/start.out")"
        echo "0 of $cases passed"
        exit 1
    fi
    if ! tar -C "$repo" -cf - deploy \
        | timeout -k 10 120 docker exec -i "$box" bash -c 'mkdir -p /repo && tar -xf - --no-same-owner -C /repo'; then
        echo "STOPPED: couldn't copy deploy/ into the container."
        echo "0 of $cases passed"
        exit 1
    fi
    # In the background, so a trapped signal ends the wait at once and the EXIT trap removes the container: a command in
    # the foreground would hold the trap back until it ended, and this one runs in a process group of its own, which a
    # TERM from an outer timeout never reaches
    timeout -k 10 1200 docker exec -e MBTEST_CASES="${MBTEST_CASES:-}" "$box" bash /repo/deploy/tests/scripts-test.sh --inside &
    wait "$!"
    timeout -k 10 30 docker exec "$box" cat /out/results > "$work/results" 2> /dev/null || : > "$work/results"
    if [ -n "${MBTEST_SAVE:-}" ]; then
        mkdir -p "$MBTEST_SAVE"
        timeout -k 10 120 docker exec "$box" tar -cf - /out /evidence /fake 2> /dev/null | tar -xf - -C "$MBTEST_SAVE" \
            || echo "Couldn't copy the container's outputs to $MBTEST_SAVE."
    fi
    passed=$(($(awk '$1 == "ok"' "$work/results" | wc -l)))
    for n in $all; do
        if ! grep -qE "^(ok|failed|pending) $n( |\$)" "$work/results"; then
            if [ "$n" != 13 ] && [ "$n" != 20 ]; then
                printf 'FAILED: case %s: it didn'"'"'t run, because the run stopped before it\n' "$n"
            fi
        fi
    done
    finish_on_host 13
    finish_on_host 20
    echo "$passed of $cases passed"
    [ "$passed" = "$cases" ]
}

# ======================================================================================================================
# The part that runs inside the container
# ======================================================================================================================

home=/opt/minerbot
calls=/fake/calls.log
results=/out/results
github=https://github.com/24kConnectLLC/MinerBot.git
host_name=bot.murderminershub.com
# Made-up values, never real ones. The secrets among them must never appear in any output, log or argument (case 19).
bot_pw=MbtestBotPassword0123456789
bot_token=MbtestBotToken.made-up.0123456789abcdef
steam_key=MBTESTSTEAMKEY0123456789ABCDEF
client_id=100000000000000009
secret1=Mbtest-Client-Secret_0123456789abcdef
owners=100000000000000001,100000000000000002
key1=$(printf 'mbtest proxy key one' | sha256sum | cut -c1-64)
pw1=MbtestDbPassword0123456789abcdefghijklmn
aud=$(printf 'mbtest access audience' | sha256sum | cut -c1-64)
cpanel_secret=MBTEST-CPANEL-SECRET-0123
private_secret=MBTEST-PRIVATE-600-SECRET
steam_code=MBTEST-STEAM-CODE-7Q2X
# Images from before PR 3: the bot's two, without a revision label, and an older dashboard
old_bot=sha256:0000000000000000000000000000000000000000000000000000000000000b01
older_bot=sha256:0000000000000000000000000000000000000000000000000000000000000b00
old_dashboard=sha256:0000000000000000000000000000000000000000000000000000000000000d00
old_dashboard_label=0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c

G() { /usr/bin/git "$@"; }
O() { /usr/bin/openssl "$@"; }

stop() {   # stop <why>: the cases can't run at all
    echo "STOPPED: $1"
    exit 1
}

# ---- Checks and verdicts

begin() {   # begin <case number> <message>
    case_no=$1
    case_msg=$2
    notes=''
    out=/out/c$case_no
    mkdir -p "$out"
    status=''
    last=/dev/null
    run_mark=$(wc -l < "$calls")
}

problem() {   # problem <what's wrong> [<what came back>]: notes why the case fails
    notes+="    $1"$'\n'
    if [ -n "${2:-}" ]; then
        notes+=$(printf '%s\n' "$2" | sed -n '1,30{s/\r$//;s/^\(.\{200\}\).*/\1 .../;s/^/    | /;p}')$'\n'
    fi
}

verdict() {
    keep_evidence
    harvest_secrets
    settle
    local word=ok
    if [ -n "$notes" ]; then word=failed; fi
    case $case_no in
        13 | 20)
            printf '%s' "$notes" > "/out/notes-$case_no"
            printf '%s\n' "$case_msg" > "/out/msg-$case_no"
            echo "pending $case_no $word" >> "$results"
            ;;
        *)
            if [ "$word" = ok ]; then
                printf 'ok: case %s: %s\n' "$case_no" "$case_msg"
            else
                printf 'FAILED: case %s: %s\n' "$case_no" "$case_msg"
                printf '%s' "$notes"
            fi
            echo "$word $case_no" >> "$results"
            ;;
    esac
}

excerpt() {   # excerpt [<file>] [<lines>]: the end of a run's output
    tail -n "${2:-15}" "${1:-$last}" 2> /dev/null
}

said() {   # said <text> [<file>]: the run's output (or the file) holds the text
    grep -qF -- "$1" "${2:-$last}" 2> /dev/null
}

expect_exit() {   # expect_exit <code> <what>
    if [ "$status" != "$1" ]; then
        problem "$2 ended with exit code $status, not $1" "$(excerpt)"
    fi
}

expect_said() {   # expect_said <text> <what> [<file>]
    said "$1" "${3:-$last}" || problem "$2: it didn't say \"$1\"" "$(excerpt "${3:-$last}")"
}

# ---- Running the scripts

in_home() {   # in_home <name> <seconds> <command...>: runs it in /opt/minerbot without a terminal; its output in $last
    local name=$1 limit=$2
    shift 2
    run_mark=$(wc -l < "$calls")
    last=$out/$name.out
    (cd "$home" && timeout -k 5 "$limit" "$@") < /dev/null > "$last" 2>&1
    status=$?
}

backup_run() {   # backup_run <name> <seconds> <command...>: like in_home, with stdout and stderr apart; $last is stderr
    local name=$1 limit=$2
    shift 2
    run_mark=$(wc -l < "$calls")
    (cd "$home" && timeout -k 5 "$limit" "$@") < /dev/null > "$out/$name.out" 2> "$out/$name.err"
    status=$?
    last=$out/$name.err
}

run_calls() {   # the calls the last run made, each with its time limit, arguments and environment's names
    tail -n +"$((run_mark + 1))" "$calls"
}

unit_calls() {   # the calls the last run's detached part made
    run_calls | grep -E '##env (.* )?MINERBOT_DETACHED( |$)'
}

args_of() {   # the calls without their environment's names
    sed 's/ ##env .*//'
}

line_of() {   # line_of <extended regex>: the first line number in the last run's calls that matches, or 0
    local n
    n=$(run_calls | args_of | grep -nE -m 1 -- "$1" | cut -d: -f1)
    echo "${n:-0}"
}

in_order() {   # in_order <what> <regex>...: each regex matches a call of the last run, in that order
    local what=$1 previous=0 n re
    shift
    for re in "$@"; do
        n=$(run_calls | args_of | tail -n +"$((previous + 1))" | grep -nE -m 1 -- "$re" | cut -d: -f1)
        if [ -z "$n" ]; then
            problem "$what: no call matching /$re/ after the ones before it" "$(run_calls | args_of | tail -n 25)"
            return 1
        fi
        previous=$((previous + n))
    done
}

hold_lock() {   # hold_lock <lock file>: another process holds that lock, for at most 30 seconds; its PID is in $holder
    local n
    flock -o "$1" sleep 30 &
    holder=$!
    for n in $(seq 1 50); do
        if ! flock -n "$1" true; then return 0; fi
        sleep 0.1
    done
    problem "the test couldn't take the lock $1 (after $n tries)"
}

let_go() {   # let_go: the process hold_lock started ends
    kill "$holder" 2> /dev/null
    wait "$holder" 2> /dev/null
}

# By the time each log last changed, not by name: a second run of an action in the same second gets <UTC>-2-<action>.log,
# whose name sorts before the first run's <UTC>-<action>.log
last_log() {   # last_log <action>: the newest run log of that action
    find "$home/logs" -maxdepth 1 -name "*-$1.log" -printf '%T@ %p\n' 2> /dev/null | sort -n | tail -n 1 | cut -d' ' -f2-
}

direct() {   # direct <name> <seconds> <NAME=value>... -- <command...>: a detached part started directly, with its own log
    local name=$1 limit=$2
    local -a values=()
    shift 2
    while [ "$1" != -- ]; do
        values+=("$1")
        shift
    done
    shift
    run_log=$home/logs/29991231T000000Z-$name.log
    install -d -m 700 "$home/logs"
    rm -f "$run_log"
    in_home "$name" "$limit" env MINERBOT_DETACHED=1 MINERBOT_LOG="$run_log" "${values[@]}" "$@"
    if [ -f "$run_log" ]; then
        cat "$run_log" >> "$last"
    fi
}

# Types into a real pseudo-terminal through script: each answer goes in once the text before it has appeared since the
# previous answer. "!text" waits a little longer before typing, for a question that turns echo off first; "FILE:path"
# waits for that file instead. The transcript is in $last, its exit code in $status.
converse() {   # converse <name> <command> [<wait for> <answer>]...
    local name=$1 command=$2 fifo feeder
    shift 2
    fifo=/tmp/mbtest-$name.fifo
    rm -f "$fifo" "$fifo.done"
    mkfifo "$fifo"
    last=$out/$name.txt
    : > "$last"
    run_mark=$(wc -l < "$calls")
    feed "$last" "$fifo" "$@" &
    feeder=$!
    (cd "$home" && timeout -k 5 150 script -qefc "$command" /dev/null) < "$fifo" > "$last" 2>&1
    status=$?
    touch "$fifo.done"
    wait "$feeder" 2> /dev/null
    rm -f "$fifo" "$fifo.done"
    tr -d '\r' < "$last" > "$last.tmp" && mv "$last.tmp" "$last"
    echo "$status" > "$out/$name.status"
}

feed() {   # feed <transcript> <fifo> [<wait for> <answer>]...
    local transcript=$1 fifo=$2 offset=0 want text answer ends
    shift 2
    exec 3> "$fifo"
    while [ $# -ge 2 ]; do
        want=$1
        answer=$2
        shift 2
        text=${want#!}
        ends=$((SECONDS + 40))
        while :; do
            if [[ $want == FILE:* ]]; then
                [ ! -e "${want#FILE:}" ] || break
            elif tail -c +"$((offset + 1))" "$transcript" 2> /dev/null | tr -d '\r' | grep -qF -- "$text"; then
                break
            fi
            if [ -e "$fifo.done" ] || [ "$SECONDS" -ge "$ends" ]; then
                echo "(the test waited in vain for: $text)" >> "$transcript.feed"
                exec 3>&-
                return 0
            fi
            sleep 0.1
        done
        offset=$(stat -c %s "$transcript")
        if [[ $want == '!'* ]]; then sleep 0.7; else sleep 0.2; fi
        printf '%s' "$answer" >&3
    done
    ends=$((SECONDS + 150))
    until [ -e "$fifo.done" ] || [ "$SECONDS" -ge "$ends" ]; do
        sleep 0.1
    done
    exec 3>&-
}

# paste_of <PEM file>: the block as a terminal gets it from a paste with CRLF line ends, every line ending in \r\n,
# which arrives as a blank line after each line. $(...) takes the last \n away, so the callers add it back with $'\n'.
paste_of() {
    sed 's/$/\r/' "$1"
}

# ---- The state of the server, saved and restored between cases

# Ends whatever a case left running: units, stand-ins, scripts, their timeouts and followers, lock holders, and
# nginx_workers' processes. AlmaLinux's coreutils is one program, so a timeout's command line starts
# "/usr/bin/coreutils --coreutils-prog-shebang=timeout".
settle() {
    local p pid cmd
    for p in /proc/[0-9]*; do
        pid=${p#/proc/}
        if [ "$pid" = 1 ] || [ "$pid" = "$$" ] || [ "$pid" = "$BASHPID" ]; then continue; fi
        cmd=$(tr '\0' ' ' < "$p/cmdline" 2> /dev/null) || continue
        case $cmd in
            *--mbtest-runner* | *"/usr/local/bin/"* | *"/opt/minerbot/"* | *"/scripts/restartsrv_nginx"* \
                | *"/usr/sbin/sendmail"* | "timeout "* | *"--coreutils-prog-shebang=timeout "* | "flock "* | "script "* \
                | "nginx: "*)
                kill -KILL "$pid" 2> /dev/null || true
                ;;
        esac
    done
    rm -f /fake/units/*.pid
}

# The dashboard's socket folder and its tmpfiles.d line are saved too once a state has them. A socket can't be saved:
# the stand-ins make it again while the dashboard's container runs.
snapshot() {   # snapshot <name>
    local -a extra=()
    if [ -e /run/minerbot-dashboard ]; then extra+=(run/minerbot-dashboard); fi
    if [ -e /etc/tmpfiles.d/minerbot-dashboard.conf ]; then extra+=(etc/tmpfiles.d/minerbot-dashboard.conf); fi
    tar -C / --warning=no-file-ignored -cpf "/snap/$1.tar" opt/minerbot etc/nginx etc/cron.d srv fake/docker fake/db \
        fake/nginx work etc/passwd etc/group etc/shadow etc/gshadow "${extra[@]}" || stop "couldn't save the state $1"
}

restore() {   # restore <name>
    settle
    rm -rf /opt/minerbot /etc/nginx /etc/cron.d /srv /fake/docker /fake/db /fake/nginx /work /fake/units /fake/clock \
        /fake/mail /tmp/mbtest-* /run/minerbot-dashboard /etc/tmpfiles.d/minerbot-dashboard.conf /var/run/nginx.pid
    mkdir -p /fake/units
    tar -C / -xpf "/snap/$1.tar" || problem "couldn't restore the state $1"
}

keep_evidence() {   # every log and unit output of the case, for case 19
    mkdir -p "/evidence/c$case_no"
    if [ -d "$home/logs" ]; then cp -a "$home/logs" "/evidence/c$case_no/" 2> /dev/null || true; fi
    cp -a /fake/units/. "/evidence/c$case_no/units" 2> /dev/null || true
}

harvest_secrets() {   # the secrets of the case's state, for case 19
    local f
    {
        if [ -f "$home/dashboard.env" ]; then
            sed -n 's/^Dashboard__ProxyKey=//p; s/^Discord__ClientSecret=//p; s/.*;Pwd=\([^;]*\);.*/\1/p' "$home/dashboard.env"
        fi
        for f in "$home/dashboard.env.previous" "$home/dashboard.env.next"; do
            if [ -f "$f" ]; then sed -n 's/^Dashboard__ProxyKey=//p; s/.*;Pwd=\([^;]*\);.*/\1/p' "$f"; fi
        done
        if [ -f /etc/nginx/minerbot/proxy-key.conf ]; then sed -n 's/.*"\([0-9a-f]\{64\}\)".*/\1/p' /etc/nginx/minerbot/proxy-key.conf; fi
        if [ -f /fake/db/password ]; then cat /fake/db/password; fi
        if [ -f "$home/bot.env" ]; then sed -n 's/^BOTTOKEN=//p; s/^STEAMWEB_PUB_KEY=//p' "$home/bot.env"; fi
    } >> /fake/secrets.txt 2> /dev/null
}

fingerprint() {   # fingerprint <path>...: one line per file below the paths: its mode, its sha256 and its name
    local f
    find "$@" \( -type f -o -type l \) -print 2> /dev/null | LC_ALL=C sort | while IFS= read -r f; do
        printf '%s %s %s\n' "$(stat -c %a "$f")" "$(sha256sum < "$f" | cut -c1-64)" "$f"
    done
}

home_state() {   # every file the scripts could change, but logs, locks, src and state/tmp
    local f
    find "$home" /etc/nginx /etc/cron.d -path "$home/src" -prune -o -path "$home/logs" -prune -o -path "$home/state/tmp" -prune \
        -o -path "$home/state/lock" -prune -o -path "$home/state/lock.info" -prune -o -path "$home/state/backup.lock" -prune \
        -o \( -type f -o -type l \) -print 2> /dev/null | LC_ALL=C sort | while IFS= read -r f; do
        printf '%s %s %s\n' "$(stat -c %a "$f")" "$(sha256sum < "$f" | cut -c1-64)" "$f"
    done
}

same_state() {   # same_state <what> <state before> <state now>
    if [ "$2" != "$3" ]; then
        problem "$1" "$(diff <(printf '%s\n' "$2") <(printf '%s\n' "$3") | grep '^[<>]' | head -n 12)"
    fi
}

docker_state() { fingerprint /fake/docker/tags /fake/docker/ctr; }

nginx_files() {   # the four files MinerBot puts into nginx, as the stand-in nginx's "accepted" lists them
    local f
    for f in /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/minerbot/origin-cert.pem /etc/nginx/minerbot/origin-key.pem \
        /etc/nginx/minerbot/proxy-key.conf; do
        if [ -f "$f" ]; then sha256sum < "$f" | cut -c1-64; else echo absent; fi
    done
}

image_of() {   # image_of <commit> bot|dashboard: the ID the stand-in docker gives a build of that commit
    printf 'sha256:%s\n' "$(printf '%s %s' "$1" "minerbot-$2" | sha256sum | cut -c1-64)"
}

tag_of() {   # tag_of <name:tag>: the image ID the tag points at, or nothing
    cat "/fake/docker/tags/${1//:/@}" 2> /dev/null
}

ctr_image() {   # ctr_image <container>: the image ID it runs, or nothing
    local id rest
    if { read -r id rest < "/fake/docker/ctr/$1"; } 2> /dev/null; then echo "$id"; fi
}

set_main() {   # set_main <commit>: GitHub's main moves there
    G -C /work/origin.git update-ref refs/heads/main "$1"
}

file_table() {   # where each deploy file is installed, and its mode
    printf '%s\n' "compose.yml $home/compose.yml 600" "common.sh $home/common.sh 600" \
        "dashboard-rights.sql $home/dashboard-rights.sql 600" "rights-list.sql $home/rights-list.sql 600" \
        "minerbot-dashboard.conf $home/minerbot-dashboard.conf 600" "deploy.sh $home/deploy.sh 700" \
        "rollback.sh $home/rollback.sh 700" "setup-dashboard.sh $home/setup-dashboard.sh 700" \
        "fill-secrets.sh $home/fill-secrets.sh 700" "backup.sh $home/backup.sh 700" "status.sh $home/status.sh 700" \
        "README.txt $home/README.txt 600" "minerbot.cron /etc/cron.d/minerbot 644" \
        "minerbot-dashboard.tmpfiles /etc/tmpfiles.d/minerbot-dashboard.conf 644"
}

expect_installed() {   # expect_installed <what>: every deploy file is in place with its mode and the repository's text
    local name target mode
    while read -r name target mode; do
        if [ ! -f "$target" ]; then
            problem "$1: $target isn't there"
            continue
        fi
        [ "$(stat -c %a "$target")" = "$mode" ] || problem "$1: $target has mode $(stat -c %a "$target"), not $mode"
        if [ "$name" = README.txt ]; then
            ! grep -q '{{ORIGIN_CERT_ENDS}}' "$target" || problem "$1: README.txt still has {{ORIGIN_CERT_ENDS}}"
        else
            cmp -s "/repo/deploy/$name" "$target" || problem "$1: $target isn't deploy/$name"
        fi
    done < <(file_table)
}

render_readme_here() {   # README.txt as install_deploy_files renders it, from the repository's copy
    (
        # shellcheck source=/dev/null
        source /repo/deploy/common.sh && render_readme /repo/deploy/README.txt
    ) > "$home/README.txt"
    chmod 600 "$home/README.txt"
}

write_dashboard_env() {   # write_dashboard_env <proxy key> <password> <admins>: the file as setup writes it
    local header1 header2
    header1=$(sed -n 's/^env_header_1="\(.*\)"$/\1/p' /repo/deploy/setup-dashboard.sh)
    header2=$(sed -n 's/^env_header_2="\(.*\)"$/\1/p' /repo/deploy/setup-dashboard.sh)
    [ -n "$header1" ] && [ -n "$header2" ] || stop "setup-dashboard.sh's env_header_1 and env_header_2 lines weren't found"
    {
        printf '%s\n' "$header1" "$header2"
        printf '%s\n' "Dashboard__PublicOrigin=https://$host_name" "Dashboard__ProxyKey=$1" "Dashboard__Owners=$owners" \
            "Dashboard__Admins=$3" "Discord__ClientId=$client_id" "Discord__ClientSecret=$secret1" \
            "CloudflareAccess__TeamDomain=" "CloudflareAccess__Audience="
        printf 'ConnectionStrings__DefaultConnection=Server=127.0.0.1;Port=3306;Database=website_db;Uid=minerbot-dashboard;Pwd=%s;SslMode=Disabled;Connection Timeout=15;CharSet=utf8mb4;Maximum Pool Size=6;\n' "$2"
        printf 'ConnectionStrings__BotConnection=Server=127.0.0.1;Port=3306;Database=minerbot_db;Uid=minerbot-dashboard;Pwd=%s;SslMode=Disabled;Connection Timeout=15;CharSet=utf8mb4;Maximum Pool Size=6;\n' "$2"
    } > "$home/dashboard.env"
    chmod 600 "$home/dashboard.env"
}

a_dump() {   # a_dump <file>: a whole dump, gzipped, 600
    printf '%s\n' "-- MariaDB dump (made up)" "-- Dump completed" | gzip -6 > "$1"
    chmod 600 "$1"
}

socket_folder() {   # the dashboard's socket folder as systemd-tmpfiles makes it: 2750, owner 1654, group nobody
    install -d -m 2750 -o 1654 -g nobody /run/minerbot-dashboard
    chmod 2750 /run/minerbot-dashboard
}

a_socket() {   # a_socket <path> <owner> <group> <mode>: a real socket file, as a process leaves it when it ends
    rm -f "$1"
    python3 -c 'import socket, sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])' "$1" \
        && chown "$2:$3" "$1" && chmod "$4" "$1"
}

# A stand-in nginx, as ps shows the real one: a master whose PID is in /var/run/nginx.pid, as the server's nginx.conf
# says, and its workers, each one's command line starting with its title, as in "nginx: worker process". A USR1 makes
# the master start one more worker, as a reload does (nginx_reload). settle ends them all.
nginx_workers() {   # nginx_workers <worker title>...: returns once the master and its workers run
    local master='' n
    rm -f /var/run/nginx.pid
    cat > /tmp/mbtest-nginx-master.sh << 'EOF'
trap '(exec -a "nginx: worker process" sleep 600) &' USR1
for title in "$@"; do
    (exec -a "$title" sleep 600) &
done
echo "$$" > /var/run/nginx.pid
while :; do
    sleep 1 &
    wait "$!"
done
EOF
    (exec -a "nginx: master process" bash /tmp/mbtest-nginx-master.sh "$@") > /dev/null 2>&1 &
    # shellcheck disable=SC2009 # pgrep is the stand-in here, so ps counts the stand-in nginx's workers
    for n in $(seq 1 50); do
        { read -r master < /var/run/nginx.pid; } 2> /dev/null || master=''
        if [ -n "$master" ] && [ "$(ps -o args= --ppid "$master" | grep -c '^nginx: ')" = "$#" ]; then
            return 0
        fi
        sleep 0.1
    done
    problem "the stand-in nginx's master and $# worker(s) didn't start (after $n tries)"
}

# shellcheck disable=SC2009 # pgrep is the stand-in here, so ps counts the stand-in nginx's workers
nginx_reload() {   # nginx_reload: one more worker under the stand-in master, as a reload starts; returns once it runs
    local master='' before n
    { read -r master < /var/run/nginx.pid; } 2> /dev/null || master=''
    before=$(ps -o args= --ppid "${master:-0}" | grep -c '^nginx: ')
    kill -USR1 "${master:-0}" 2> /dev/null || { problem "there's no stand-in nginx to reload"; return 0; }
    for n in $(seq 1 50); do
        if [ "$(ps -o args= --ppid "$master" | grep -c '^nginx: ')" -gt "$before" ]; then
            return 0
        fi
        sleep 0.1
    done
    problem "the stand-in nginx's reload didn't start a worker (after $n tries)"
}

# ---- Building the states

prepare() {
    local f name
    echo "Installing what the server has (dnf), and the stand-ins."
    if ! rpm -q git-core util-linux procps-ng diffutils openssl jq shadow-utils > /dev/null 2>&1; then
        timeout -k 10 240 dnf -y -q install git-core util-linux procps-ng diffutils openssl jq shadow-utils > /out/dnf.log 2>&1 \
            || stop "dnf couldn't install the packages: $(tail -n 5 /out/dnf.log)"
    fi
    # A clean start, also when this runs again in the same container
    rm -rf /work /opt/minerbot /etc/nginx /srv /fake /evidence /snap /etc/cron.d/minerbot /root/.gitconfig /tmp/mbtest-* \
        /run/minerbot-dashboard /etc/tmpfiles.d/minerbot-dashboard.conf /var/run/nginx.pid
    userdel mbtest-other > /dev/null 2>&1 || true
    userdel minerbot-app > /dev/null 2>&1 || true
    groupdel mbtest-other > /dev/null 2>&1 || true
    groupdel minerbot-app > /dev/null 2>&1 || true
    install -d -m 755 /usr/local/lib/mbtest /scripts /etc/cron.d
    install -m 755 /repo/deploy/tests/fakes/record.sh /usr/local/lib/mbtest/record.sh
    for f in /repo/deploy/tests/fakes/*; do
        name=${f##*/}
        case $name in
            record.sh) ;;
            restartsrv_nginx) install -m 755 "$f" /scripts/restartsrv_nginx ;;
            sendmail) install -m 755 "$f" /usr/sbin/sendmail ;;
            *) install -m 755 "$f" "/usr/local/bin/$name" ;;
        esac
    done
    hash -r
    mkdir -p /fake/docker/tags /fake/docker/ids /fake/docker/ctr /fake/db /fake/nginx /fake/units /out /evidence /snap /work /srv
    : > "$calls"
    : > /fake/stdin.log
    G config --global user.email mbtest@example.test
    G config --global user.name mbtest
    G config --global init.defaultBranch main
    G config --global advice.detachedHead false
    G config --global url./work/origin.git.insteadOf "$github"

    echo "Making GitHub's stand-in, the certificates and nginx's folder."
    make_repo
    make_certs
    make_nginx
    printf '%s\n' "$bot_pw" "$bot_token" "$steam_key" "$secret1" "$key1" "$pw1" "$cpanel_secret" "$private_secret" \
        "$(sed -n 2p /work/certs/good-key.pem)" "$(sed -n 2p /work/certs/good2-key.pem)" > /fake/secrets.txt

    echo "Making the server's states: today's, after the move, built, set up and deployed."
    world_today
    snapshot today
    world_moved
    snapshot moved
    world_built
    snapshot built
    world_setup
    snapshot setup
    world_deployed
    snapshot deployed
}

commit_all() {   # commit_all <message>: commits the work repository and prints the commit
    G -C /work/repo add -A && G -C /work/repo commit -qm "$1" && G -C /work/repo rev-parse HEAD
}

make_repo() {
    mkdir -p /work/repo/DataAccessLibrary/Migrations /work/repo/MinerBot.Dashboard "/work/repo/MinerBot 2.0"
    cp -r /repo/deploy /work/repo/deploy
    printf -- '-- A stand-in for the first migration\n' > /work/repo/DataAccessLibrary/Migrations/0001_core.sql
    printf 'FROM scratch\n' > /work/repo/MinerBot.Dashboard/Dockerfile
    printf 'FROM scratch\n' > "/work/repo/MinerBot 2.0/Dockerfile"
    echo one > /work/repo/version.txt
    G -C /work/repo init -q -b main || stop "git init failed"
    c1=$(commit_all one) || stop "the first commit failed"
    # c2: its deploy.sh says so when it runs; c3: main after c2, with another deploy.sh
    G -C /work/repo checkout -q -b c2
    # shellcheck disable=SC2016 # $ref is deploy.sh's own text
    sed -i 's/^\(    echo "\)Deploy of \$ref for/\1C2 code: Deploy of $ref for/' /work/repo/deploy/deploy.sh
    grep -q 'C2 code: Deploy of' /work/repo/deploy/deploy.sh || stop "couldn't mark c2's deploy.sh"
    c2=$(commit_all two)
    G -C /work/repo checkout -q -b c3
    sed -i 's/C2 code: Deploy of/C3 code: Deploy of/' /work/repo/deploy/deploy.sh
    c3=$(commit_all three)
    # c4: a rollback.sh that fails bash -n
    G -C /work/repo checkout -q -b c4 "$c1"
    printf 'if then\n' >> /work/repo/deploy/rollback.sh
    c4=$(commit_all four)
    # c5: a change outside deploy/, so new images and the same deploy files
    G -C /work/repo checkout -q -b c5 "$c1"
    echo five > /work/repo/version.txt
    c5=$(commit_all five)
    # c6: a new migration
    G -C /work/repo checkout -q -b c6 "$c1"
    printf -- '-- A stand-in for a second migration\n' > /work/repo/DataAccessLibrary/Migrations/0002_more.sql
    c6=$(commit_all six)
    # feature: never merged into main
    G -C /work/repo checkout -q -b feature "$c1"
    echo feature > /work/repo/version.txt
    f1=$(commit_all feature)
    G -C /work/repo checkout -q main
    G clone -q --bare /work/repo /work/origin.git || stop "git clone --bare failed"
    [ -n "$c1" ] && [ -n "$c2" ] && [ -n "$c3" ] && [ -n "$c4" ] && [ -n "$c5" ] && [ -n "$c6" ] && [ -n "$f1" ] \
        || stop "couldn't make the test commits"
}

make_certs() {
    local d=/work/certs
    mkdir -p "$d"
    cert() {   # cert <name> <days> <subjectAltName> rsa|ec
        local -a keys=(-newkey rsa:2048)
        if [ "$4" = ec ]; then keys=(-newkey ec -pkeyopt ec_paramgen_curve:P-256); fi
        O req -x509 "${keys[@]}" -nodes -days "$2" -subj "/CN=$host_name" -addext "subjectAltName=$3" \
            -keyout "$d/$1-key.pem" -out "$d/$1-cert.pem" 2> /dev/null || stop "openssl couldn't make the $1 certificate"
    }
    cert good 4000 "DNS:$host_name" rsa
    cert good2 4000 "DNS:$host_name" ec
    cert wildcard 4000 "DNS:*.murderminershub.com" ec
    cert twoname 4000 "DNS:$host_name,DNS:murderminershub.com" ec
    cert short 10 "DNS:$host_name" ec
    O pkcs8 -topk8 -in "$d/good-key.pem" -out "$d/encrypted-key.pem" -passout pass:mbtest-made-up -v2 aes-256-cbc 2> /dev/null \
        || stop "openssl couldn't encrypt a key"
}

make_nginx() {   # an nginx folder laid out as cPanel's
    local i
    install -d -m 755 /etc/nginx /etc/nginx/conf.d /etc/nginx/conf.d/modules /etc/nginx/conf.d/includes-optional /etc/nginx/conf.d/users
    printf '%s\n' "include /etc/nginx/conf.d/modules/*.conf;" "user nobody;" "worker_processes 1;" \
        "error_log /var/log/nginx/error.log warn;" "pid /var/run/nginx.pid;" "" "events {" "    worker_connections 1024;" "}" "" \
        "http {" "    include /etc/nginx/mime.types;" "    server_tokens off;" "    include /etc/nginx/conf.d/*.conf;" "}" \
        > /etc/nginx/nginx.conf
    echo "load_module modules/ngx_http_pipelog_module.so;" > /etc/nginx/conf.d/modules/ngx_http_pipelog_module.conf
    printf '%s\n' "client_max_body_size 128m;" "ssl_protocols TLSv1.2 TLSv1.3;" > /etc/nginx/conf.d/ea-nginx.conf
    printf '%s\n' "server {" "    listen 80 default_server reuseport;" "    listen 443 ssl default_server reuseport;" \
        "    server_name _;" "    return 444;" "}" > /etc/nginx/conf.d/default.conf
    echo "include conf.d/users/*.conf;" > /etc/nginx/conf.d/users.conf
    printf '%s\n' "server {" "    listen 443 ssl;" "    server_name example.test;" "}" > /etc/nginx/conf.d/users/example.conf
    for i in $(seq 1 22); do echo "set_real_ip_from 198.51.$i.0/24;"; done > /etc/nginx/conf.d/includes-optional/cloudflare.conf
    echo "real_ip_header CF-Connecting-IP;" >> /etc/nginx/conf.d/includes-optional/cloudflare.conf
    # Like cPanel's own proxy file: only root can read it, and nginx -T prints it all the same
    printf '%s\n' "proxy_set_header cPanel-localhost \"$cpanel_secret\";" "set \$mbtest_private \"$private_secret\";" \
        > /etc/nginx/conf.d/includes-optional/cpanel-proxy-xt.conf
    chmod 644 /etc/nginx/nginx.conf /etc/nginx/conf.d/*.conf /etc/nginx/conf.d/*/*.conf
    chmod 600 /etc/nginx/conf.d/includes-optional/cpanel-proxy-xt.conf
    # cPanel's folder of programs it runs each time it rebuilds nginx's global files
    install -d -m 755 /etc/nginx/ea-nginx /etc/nginx/ea-nginx/config-scripts /etc/nginx/ea-nginx/config-scripts/global
}

world_today() {   # the server as it is before PR 3
    local f
    install -d -m 700 "$home"
    G clone -q "$github" "$home/src" || stop "git couldn't clone GitHub's stand-in"
    install -m 600 /repo/deploy/tests/fixtures/compose-before-pr3.yml "$home/compose.yml"
    for f in deploy.sh rollback.sh fill-secrets.sh setup-login.sh move-docker-storage.sh; do
        printf '#!/bin/bash\n# Today'"'"'s %s on the server, before PR 3 (a stand-in)\necho "today'"'"'s %s"\n' "$f" "$f" > "$home/$f"
        chmod 700 "$home/$f"
    done
    printf 'Today'"'"'s README.txt on the server, before PR 3 (a stand-in)\n' > "$home/README.txt"
    chmod 600 "$home/README.txt"
    {
        echo "STEAM_QRCODE_AUTH=true"
        echo "ConnectionStrings__DefaultConnection=Server=127.0.0.1;Port=3306;Database=website_db;Uid=mbtest_bot;Pwd=$bot_pw;SslMode=Disabled;Connection Timeout=30;"
        echo "ConnectionStrings__BotConnection=Server=127.0.0.1;Port=3306;Database=minerbot_db;Uid=mbtest_bot;Pwd=$bot_pw;SslMode=Disabled;Connection Timeout=30;"
        echo "BOTTOKEN=$bot_token"
        echo "WEBHOOKCHANNEL=123456789012345678"
        echo "STEAMWEB_PUB_KEY=$steam_key"
        echo "STEAMUSER_NAME=mbtest_steam"
    } > "$home/bot.env"
    chmod 600 "$home/bot.env"
    : > "/fake/docker/ids/$old_bot"
    : > "/fake/docker/ids/$older_bot"
    echo "$old_bot" > /fake/docker/tags/minerbot-bot@current
    echo "$older_bot" > /fake/docker/tags/minerbot-bot@previous
    echo "$older_bot" > /fake/docker/tags/minerbot-bot@1a2b3c4d5e6f
    echo "$old_bot true 0 2026-11-01T00:00:00.000000000Z none 4001 $(printf 'bot container' | sha256sum | cut -c1-64) c124d0e1f2a3" \
        > /fake/docker/ctr/minerbot-bot
}

world_moved() {   # after the move, by hand: today's files kept in old/, server.conf, and the deploy files' two scripts
    local f
    install -d -m 700 "$home/state" "$home/state/tmp" "$home/logs" "$home/old"
    for f in compose.yml deploy.sh rollback.sh fill-secrets.sh setup-login.sh move-docker-storage.sh README.txt; do
        cp -p "$home/$f" "$home/old/$f"
    done
    rm -f "$home/setup-login.sh" "$home/move-docker-storage.sh"
    printf '%s\n' "# This server's own names (made up for the test)" "SITE_DB=website_db" "BOT_DB=minerbot_db" \
        "BOT_READY_PATTERN=Gateway +Ready" > "$home/server.conf"
    chmod 600 "$home/server.conf"
    install -m 700 /repo/deploy/deploy.sh "$home/deploy.sh"
    install -m 600 /repo/deploy/common.sh "$home/common.sh"
}

world_built() {   # after ./deploy.sh main --only dashboard --no-start
    local name target mode id
    while read -r name target mode; do
        if [ "$name" != README.txt ]; then install -m "$mode" "/repo/deploy/$name" "$target"; fi
    done < <(file_table)
    render_readme_here
    # The dashboard's socket folder, as the deploy's systemd-tmpfiles made it (case 2 checks the real one does)
    socket_folder
    echo "$c1" > "$home/state/deploy-files.commit"
    chmod 600 "$home/state/deploy-files.commit"
    id=$(image_of "$c1" dashboard)
    echo "$c1" > "/fake/docker/ids/$id"
    echo "$id" > /fake/docker/tags/minerbot-dashboard@new
    echo 1 > /fake/db/version
    install -d -m 700 /srv/backups /srv/backups/minerbot
    a_dump "/srv/backups/minerbot/minerbot-20261105T080000Z-pre-deploy.sql.gz"
    touch -d '-1 hour' /srv/backups/minerbot/minerbot-20261105T080000Z-pre-deploy.sql.gz
    printf '%s\n' "2026-11-05T08:00:00Z started pre-deploy" "2026-11-05T08:00:01Z ok minerbot-20261105T080000Z-pre-deploy.sql.gz 4.0K" \
        > /srv/backups/minerbot/backup.log
    chmod 600 /srv/backups/minerbot/backup.log
    printf '2026-11-05T08:00:30Z\tdeploy\tdashboard\t%s\tbuilt\n' "${c1:0:12}" > "$home/logs/history.log"
    chmod 600 "$home/logs/history.log"
}

world_setup() {   # after the first setup: the login, dashboard.env and nginx's files
    install -d -m 700 /etc/nginx/minerbot
    install -m 600 /work/certs/good-cert.pem /etc/nginx/minerbot/origin-cert.pem
    install -m 600 /work/certs/good-key.pem /etc/nginx/minerbot/origin-key.pem
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$key1" > /etc/nginx/minerbot/proxy-key.conf
    chmod 600 /etc/nginx/minerbot/proxy-key.conf
    install -m 644 "$home/minerbot-dashboard.conf" /etc/nginx/conf.d/minerbot-dashboard.conf
    # pipelog.conf, with the module loaded, and cPanel's hook that keeps it in step, both as nginx_change writes them
    printf 'pipelog off;\n' > /etc/nginx/minerbot/pipelog.conf
    chmod 600 /etc/nginx/minerbot/pipelog.conf
    ( # shellcheck source=/dev/null
        source /repo/deploy/common.sh && pipelog_hook_text) > /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog
    chmod 755 /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog
    # What nginx runs since the setup's reload
    cp /work/certs/good-cert.pem /fake/nginx/served-cert.pem
    cp /etc/nginx/minerbot/proxy-key.conf /fake/nginx/loaded-proxy-key.conf
    touch /fake/db/login-localhost /fake/db/login-127.0.0.1 /fake/db/dashboard-applied
    echo "$pw1" > /fake/db/password
    write_dashboard_env "$key1" "$pw1" ""
    render_readme_here
    printf '2026-11-05T08:10:00Z\tsetup\tdashboard\t-\tok\tlogin created, nginx files, dashboard.env\n' >> "$home/logs/history.log"
}

world_deployed() {   # after ./deploy.sh: both services on images built from c1
    local dash bot now
    dash=$(image_of "$c1" dashboard)
    bot=$(image_of "$c1" bot)
    now=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)
    echo "$c1" > "/fake/docker/ids/$bot"
    echo "$old_dashboard_label" > "/fake/docker/ids/$old_dashboard"
    echo "$bot" > /fake/docker/tags/minerbot-bot@new
    echo "$bot" > /fake/docker/tags/minerbot-bot@current
    echo "$old_bot" > /fake/docker/tags/minerbot-bot@previous
    echo "$dash" > /fake/docker/tags/minerbot-dashboard@current
    echo "$old_dashboard" > /fake/docker/tags/minerbot-dashboard@previous
    echo "$dash true 0 $now healthy 5101 $(printf 'dashboard container' | sha256sum | cut -c1-64) c124d0e1f2a3" \
        > /fake/docker/ctr/minerbot-dashboard
    echo "$bot true 0 $now none 4102 $(printf 'bot container two' | sha256sum | cut -c1-64) c124d0e1f2a3" > /fake/docker/ctr/minerbot-bot
    a_dump /srv/backups/minerbot/minerbot-20261105T091700Z-nightly.sql.gz
    printf '%s\n' "2026-11-05T09:17:00Z started nightly" "2026-11-05T09:17:01Z ok minerbot-20261105T091700Z-nightly.sql.gz 4.0K" \
        >> /srv/backups/minerbot/backup.log
    printf '2026-11-05T08:20:00Z\tdeploy\tdashboard,bot\t%s\tok\n' "${c1:0:12}" >> "$home/logs/history.log"
}

# ======================================================================================================================
# The cases
# ======================================================================================================================

case_1() {
    begin 1 "deploy.sh refuses before anything changes: a bad ref, an unknown option, --only foo, no server.conf, a bad name in it, the dashboard without dashboard.env, a held lock, a commit not on main, and an origin that isn't GitHub's MinerBot as git config writes it"
    local before
    restore moved
    before=$(home_state)
    refused() {   # refused <name> <text> <command...>
        local name=$1 text=$2
        shift 2
        in_home "$name" 60 "$@"
        expect_exit 1 "$name"
        expect_said "$text" "$name"
        if run_calls | grep -qE '^(docker|systemd-run|git) '; then
            problem "$name: it called docker, git or systemd-run before refusing" "$(run_calls | args_of | head -n 5)"
        fi
    }
    refused bad-ref "That isn't a branch, tag or commit name." ./deploy.sh 'main..other'
    refused tilde-ref "That isn't a branch, tag or commit name." ./deploy.sh 'main~1'
    refused space-ref "That isn't a branch, tag or commit name." ./deploy.sh 'a b'
    refused long-ref "That isn't a branch, tag or commit name." ./deploy.sh "$(printf 'a%.0s' $(seq 1 101))"
    refused option "Unknown option --fast." ./deploy.sh --fast
    refused only-foo "--only takes bot or dashboard." ./deploy.sh --only foo
    mv "$home/server.conf" /tmp/mbtest-server.conf
    refused no-conf "$home/server.conf is missing." ./deploy.sh --only bot
    mv /tmp/mbtest-server.conf "$home/server.conf"
    sed -i 's/^SITE_DB=.*/SITE_DB=web-site/' "$home/server.conf"
    refused bad-name "SITE_DB in $home/server.conf must be the website's database name" ./deploy.sh --only bot
    sed -i 's/^SITE_DB=.*/SITE_DB=website_db/' "$home/server.conf"
    sed -i 's/^BOT_READY_PATTERN=.*/BOT_READY_PATTERN=(/' "$home/server.conf"
    refused bad-pattern "BOT_READY_PATTERN in $home/server.conf must be a pattern grep -E accepts" ./deploy.sh --only bot
    sed -i 's/^BOT_READY_PATTERN=.*/BOT_READY_PATTERN=Gateway +Ready/' "$home/server.conf"
    refused one-database "SITE_DB and BOT_DB in $home/server.conf must name two databases that exist" \
        env FAKE_DB_SCHEMATA=1 ./deploy.sh --only bot
    refused no-env "There's no dashboard.env yet." ./deploy.sh
    expect_said "To deploy only the bot meanwhile: --only bot." no-env
    hold_lock "$home/state/lock"
    echo "setup, started 2026-11-06T03:00:00Z, log $home/logs/20261106T030000Z-setup.log" > "$home/state/lock.info"
    refused lock "Another run holds the lock: setup, started 2026-11-06T03:00:00Z" ./deploy.sh --only bot
    let_go
    same_state "the refusals in the foreground changed files" "$before" "$(home_state)"

    in_home not-main 60 ./deploy.sh feature --only bot --no-start
    expect_exit 1 "a commit not on main"
    expect_said "isn't one of main's own commits: it isn't merged into main" "a commit not on main"
    if run_calls | grep -qE '^docker \[[^]]*\] build '; then problem "a commit not on main: it built something"; fi

    # Less than 5 GiB free on /srv: the unit's preflight refuses before it fetches or builds
    in_home full-srv 60 env FAKE_DF=/srv=1000 ./deploy.sh --only bot
    expect_exit 1 "a deploy with /srv nearly full"
    expect_said "Less than 5 GiB is free on /srv" "a deploy with /srv nearly full"
    ! run_calls | grep -qE '^(docker \[[^]]*\] build|git \[[^]]*\] .* fetch) ' || problem "a deploy with /srv nearly full fetched or built"

    G -C "$home/src" config remote.origin.url https://github.com/someone-else/MinerBot.git
    G config --global --add url./work/origin.git.insteadOf https://github.com/someone-else/MinerBot.git
    [ "$(G -C "$home/src" remote get-url origin)" = /work/origin.git ] \
        || problem "the test's premise: git remote get-url should follow insteadOf to /work/origin.git"
    in_home wrong-origin 60 ./deploy.sh --only bot --no-start
    expect_exit 1 "a wrong origin"
    expect_said "src's origin is https://github.com/someone-else/MinerBot.git in its git config, not https://github.com/24kConnectLLC/MinerBot" "a wrong origin"
    if run_calls | grep -qE '^git \[[^]]*\] .* fetch '; then problem "a wrong origin: it fetched before refusing"; fi
    if run_calls | grep -qE '^docker \[[^]]*\] build '; then problem "a wrong origin: it built something"; fi
    G -C "$home/src" config remote.origin.url "$github"
    same_state "the refused deploys installed or changed files" "$before" "$(home_state)"
    verdict
}

case_2() {
    begin 2 "the first build, --only dashboard --no-start: every file installed with its mode, only the dashboard built on two CPUs with its revision label, a dump because migrations are due, the migration with the bot's connection string only in its environment, no rights and no swap, history built"
    local log run expected value
    restore moved
    in_home first 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the first build"
    log=$(last_log deploy)
    expect_installed "the first build"
    [ "$(cat "$home/state/deploy-files.commit" 2> /dev/null)" = "$c1" ] || problem "state/deploy-files.commit doesn't name $c1"
    said "not installed yet" "$home/README.txt" || problem "README.txt doesn't say the certificate is not installed yet"
    expected="docker [timeout -k 30 1200] build --pull --network host --cpu-quota 200000 --cpu-period 100000 --label org.opencontainers.image.revision=$c1 -f $home/src/MinerBot.Dashboard/Dockerfile -t minerbot-dashboard:new $home/src"
    [ "$(run_calls | args_of | grep -E '^docker \[[^]]*\] build ')" = "$expected" ] \
        || problem "the builds weren't exactly the dashboard's, on two CPUs, with its label" "$(run_calls | args_of | grep ' build ')"
    find /srv/backups/minerbot -name 'minerbot-*-pre-deploy.sql.gz' -newer "$home/server.conf" -perm 600 | grep -q . \
        || problem "no new pre-deploy dump (600) in /srv/backups/minerbot"
    run_calls | args_of | grep -qE '^mariadb-dump \[timeout -k 10 300\] ' || problem "mariadb-dump didn't run under timeout 300"
    run=$(run_calls | grep -E '^docker \[timeout -k 10 180\] run ')
    expected="docker [timeout -k 10 180] run --rm --pull never --name minerbot-migrate --network host --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --cap-drop ALL --security-opt no-new-privileges:true --memory 256m --pids-limit 64 -e ConnectionStrings__BotConnection minerbot-dashboard:new migrate"
    [ "$(printf '%s\n' "$run" | args_of)" = "$expected" ] || problem "the migration's command isn't the designed one" "$run"
    [[ " ${run#* ##env } " == *" ConnectionStrings__BotConnection "* ]] \
        || problem "ConnectionStrings__BotConnection wasn't in the migration's environment"
    value=$(sed -n 's/^ConnectionStrings__BotConnection=//p' "$home/bot.env")
    [ "$(cat /fake/db/migrate-value.sha256 2> /dev/null)" = "$(printf '%s' "$value" | sha256sum | cut -c1-64)" ] \
        || problem "the migration didn't get bot.env's connection string exactly"
    ! run_calls | args_of | grep -qF "$bot_pw" || problem "the bot's password is in a command's arguments"
    in_order "docker rm -f around the migration" '^docker \[timeout -k 10 30\] rm -f -v minerbot-migrate$' ' run .* migrate$' \
        '^docker \[timeout -k 10 30\] rm -f -v minerbot-migrate$'
    [ "$(cat /fake/db/version 2> /dev/null)" = 1 ] || problem "the database wasn't migrated to version 1"
    ! run_calls | grep -qE '\{stdin: (rehearsal|apply)\}|ACCOUNT LOCK' || problem "it applied rights, which --no-start doesn't"
    ! run_calls | args_of | grep -qE ' compose .* up -d --no-build' || problem "it started or swapped a service"
    tail -n 1 "$home/logs/history.log" | grep -qP "^\S+\tdeploy\tdashboard\t${c1:0:12}\tbuilt" \
        || problem "history.log's last line isn't the built line" "$(tail -n 2 "$home/logs/history.log")"
    grep -qx 'RESULT ok' "$log" 2> /dev/null || problem "the run log has no RESULT ok" "$(excerpt "$log")"
    # The dashboard's socket folder, made by systemd-tmpfiles from the installed line, under a time limit
    [ "$(stat -c '%a %u %G %F' /run/minerbot-dashboard 2> /dev/null)" = "2750 1654 nobody directory" ] \
        || problem "the socket's folder isn't 2750, owned by 1654 and nobody" "$(stat -c '%a %u %G %F' /run/minerbot-dashboard 2>&1)"
    run_calls | args_of | grep -qxF "systemd-tmpfiles [timeout -k 10 30] --create /etc/tmpfiles.d/minerbot-dashboard.conf" \
        || problem "systemd-tmpfiles didn't make the folder, under a time limit" "$(run_calls | args_of | grep tmpfiles)"

    # The same commit again: an unchanged file with a wrong mode gets its mode back, and src keeps nothing but the commit
    chmod 644 "$home/rollback.sh"
    touch "$home/src/MinerBot.Dashboard/stray.txt"
    in_home again 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the same build again"
    [ "$(stat -c %a "$home/rollback.sh")" = 700 ] || problem "rollback.sh didn't get its mode 700 back"
    [ ! -e "$home/src/MinerBot.Dashboard/stray.txt" ] || problem "a file that isn't the commit's stayed in src"
    verdict
}

case_3() {
    begin 3 "deploy files: a commit that changes deploy.sh or only common.sh is installed by rename and the run goes on once with the new code, building exactly that commit while main moves on; a run with MINERBOT_REEXEC never runs again, and refuses when src is elsewhere; an older commit keeps the newer files; a script that fails bash -n or turns tracing on, or a compose.yml Compose refuses, stops it with nothing installed and no staging folder left"
    local log before traced common
    # A changed deploy.sh; main moves to c3 at the second docker info, which comes after the install
    restore built
    set_main "$c2"
    printf '#!/bin/bash\n/usr/bin/git -C /work/origin.git update-ref refs/heads/main %s\n' "$c3" > /tmp/mbtest-push-c3.sh
    in_home rerun 120 env FAKE_INFO_HOOK=/tmp/mbtest-push-c3.sh ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy of a changed deploy.sh"
    log=$(last_log deploy)
    [ "$(grep -c 'The deploy goes on with them.' "$log")" = 1 ] || problem "it didn't run again exactly once" "$(excerpt "$log" 30)"
    [ "$(grep -c 'This run goes on with the new deploy files.' "$log")" = 1 ] || problem "the second run didn't say it goes on with the new files"
    [ "$(grep -c '^C2 code: Deploy of main' "$log")" = 1 ] || problem "the new deploy.sh's code didn't run, exactly once"
    G -C /work/origin.git show "$c2:deploy/deploy.sh" | cmp -s - "$home/deploy.sh" || problem "the installed deploy.sh isn't c2's"
    [ "$(cat "$home/state/deploy-files.commit")" = "$c2" ] || problem "deploy-files.commit doesn't name c2"
    [ "$(G -C /work/origin.git rev-parse main)" = "$c3" ] || problem "the test's hook didn't move main to c3"
    [ "$(run_calls | grep -cE '^git \[[^]]*\] -C /opt/minerbot/src fetch ')" = 1 ] || problem "it fetched more or less than once"
    ! run_calls | grep -E '^git \[[^]]*\] -C /opt/minerbot/src fetch ' | grep -q MINERBOT_REEXEC || problem "the second run fetched"
    run_calls | args_of | grep -qF "build --pull --network host --cpu-quota 200000 --cpu-period 100000 --label org.opencontainers.image.revision=$c2 " \
        || problem "it didn't build c2, the commit whose files it installed"
    ! run_calls | args_of | grep -qF "revision=$c3" || problem "it built c3, which main moved to during the run"

    # MINERBOT_REEXEC set to anything: no further run
    restore built
    set_main "$c2"
    G -C "$home/src" fetch -q origin && G -C "$home/src" checkout -q --detach "$c2"
    direct reexec 120 MINERBOT_REEXEC="$c2" -- ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "a run with MINERBOT_REEXEC"
    expect_said "but this run was started again once already, so it goes on as it is." "a run with MINERBOT_REEXEC"
    ! said "The deploy goes on with them." || problem "a run with MINERBOT_REEXEC ran again"
    ! said "C2 code:" || problem "a run with MINERBOT_REEXEC switched to the new code"
    direct reexec-bad 60 MINERBOT_REEXEC=yes -- ./deploy.sh main --only dashboard --no-start
    expect_exit 1 "MINERBOT_REEXEC=yes"
    expect_said "MINERBOT_REEXEC must be the full commit whose deploy files this run installed." "MINERBOT_REEXEC=yes"

    # An older commit keeps the newer files
    restore built
    set_main "$c2"
    echo "$c2" > "$home/state/deploy-files.commit"
    before=$(home_state)
    in_home older 120 ./deploy.sh "$c1" --only dashboard --no-start
    expect_exit 0 "the deploy of an older commit"
    expect_said "Kept the deploy files of ${c2:0:12}, which are newer than ${c1:0:12}." "the deploy of an older commit"
    same_state "the deploy of an older commit changed installed files" "$before" "$(home_state)"

    # A script that fails bash -n
    restore built
    set_main "$c4"
    before=$(home_state)
    in_home broken 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 1 "the deploy of a broken script"
    expect_said "deploy/rollback.sh fails bash -n" "the deploy of a broken script"
    expect_said "didn't pass their checks (above), so none of them was installed." "the deploy of a broken script"
    same_state "the deploy of a broken script installed something" "$before" "$(home_state)"
    ! run_calls | grep -qE '^docker \[[^]]*\] build ' || problem "the deploy of a broken script built"
    [ -z "$(find "$home/state/tmp" -maxdepth 1 -name 'deploy.*' 2> /dev/null)" ] \
        || problem "the deploy of a broken script left its staging folder in state/tmp"

    # A script that turns tracing on, and a compose.yml Compose refuses: nothing is installed either
    restore built
    traced=$(new_commit "$c1" traced sh -c 'echo "set -x" >> deploy/rollback.sh') || problem "couldn't make the commit with set -x"
    set_main "$traced"
    before=$(home_state)
    in_home traced 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 1 "the deploy of a script that turns tracing on"
    expect_said "deploy/rollback.sh turns tracing on, which would print secrets." "the deploy of a script that turns tracing on"
    same_state "the deploy of a script that turns tracing on installed something" "$before" "$(home_state)"
    restore built
    set_main "$c2"
    before=$(home_state)
    in_home compose-refused 120 env FAKE_COMPOSE_CONFIG=1 ./deploy.sh main --only dashboard --no-start
    expect_exit 1 "the deploy of a compose.yml Compose refuses"
    expect_said "Compose refuses deploy/compose.yml" "the deploy of a compose.yml Compose refuses"
    same_state "the deploy of a compose.yml Compose refuses installed something" "$before" "$(home_state)"

    # A new deploy.sh is renamed into place, so a second name for the old one keeps the old bytes
    restore built
    set_main "$c2"
    cp "$home/deploy.sh" /tmp/mbtest-deploy.sh.before
    ln "$home/deploy.sh" "$home/state/.mbtest-deploy.sh"
    in_home renamed 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy that installs c2's deploy.sh"
    [ "$(stat -c %i "$home/deploy.sh")" != "$(stat -c %i "$home/state/.mbtest-deploy.sh")" ] \
        && cmp -s "$home/state/.mbtest-deploy.sh" /tmp/mbtest-deploy.sh.before || problem "deploy.sh was written in place, not renamed over"
    rm -f "$home/state/.mbtest-deploy.sh"

    # A commit that changes only common.sh: the run goes on once with the new files too
    restore built
    common=$(new_commit "$c1" common-only sh -c 'echo "# A comment that changes nothing (made up)" >> deploy/common.sh') \
        || problem "couldn't make the commit that changes only common.sh"
    set_main "$common"
    in_home common-only 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy of a changed common.sh"
    log=$(last_log deploy)
    [ "$(grep -c 'The deploy goes on with them.' "$log")" = 1 ] \
        || problem "a changed common.sh didn't make the deploy go on once with the new files" "$(excerpt "$log" 20)"

    # MINERBOT_REEXEC while src is at another commit: refused before anything else
    restore built
    set_main "$c2"
    G -C "$home/src" fetch -q origin && G -C "$home/src" checkout -q --detach "$c1"
    direct reexec-moved 60 MINERBOT_REEXEC="$c2" -- ./deploy.sh main --only dashboard --no-start
    expect_exit 1 "MINERBOT_REEXEC with src at another commit"
    expect_said "This deploy ran again for ${c2:0:12}, but src is at ${c1:0:12}." "MINERBOT_REEXEC with src at another commit"
    verdict
}

case_4() {
    begin 4 "a full deploy: the settings checked in isolation, with the socket compose.yml gives, the rights rehearsed, applied and compared, then the dashboard swapped and ready (its own socket, the public check) before the bot; history ok; run logs pruned to the newest 60"
    local log dash bot n expected
    restore deployed
    set_main "$c5"
    for n in $(seq 10 79); do
        : > "$home/logs/20260101T0000${n}Z-deploy.log"
    done
    in_home full 120 ./deploy.sh
    expect_exit 0 "the full deploy"
    log=$(last_log deploy)
    expected="docker [timeout -k 10 60] run --rm --pull never --name minerbot-dashboard-check --network none --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --tmpfs /app/keys:rw,noexec,nosuid,nodev,size=1m,mode=1777 --cap-drop ALL --security-opt no-new-privileges:true --memory 256m --pids-limit 64 --env-file /opt/minerbot/dashboard.env -e Dashboard__UnixSocket=/run/minerbot-dashboard/dashboard.sock minerbot-dashboard:new check-settings"
    [ "$(run_calls | args_of | grep -E '^docker \[[^]]*\] run .* check-settings$')" = "$expected" ] \
        || problem "the settings check isn't the designed command" "$(run_calls | args_of | grep -E ' check-settings$')"
    in_order "the order of a full deploy" \
        '^docker \[timeout -k 10 60\] run .* check-settings$' \
        "^mariadb \[timeout -k 10 30\] --batch -e DROP USER IF EXISTS 'minerbot-rights-check'@'localhost'; CREATE USER 'minerbot-rights-check'@'localhost' ACCOUNT LOCK;" \
        '^mariadb \[timeout -k 10 60\] --batch \{stdin: rehearsal\}' \
        '^mariadb \[timeout -k 10 60\] --batch \{stdin: apply\}' \
        '\{stdin: rights of minerbot-rights-check@localhost\}' \
        "--batch -e DROP USER IF EXISTS 'minerbot-rights-check'@'localhost'\$" \
        '\{stdin: rights of minerbot-dashboard@localhost\}' \
        '\{stdin: rights of minerbot-dashboard@127.0.0.1\}' \
        '^curl \[timeout -k 10 15\] -s -o /dev/null -w %\{http_code\} --max-time 10 https://bot.murderminershub.com/$' \
        '^docker \[timeout -k 10 120\] compose -f /opt/minerbot/compose.yml --project-directory /opt/minerbot up -d --no-build --no-deps dashboard$' \
        '^curl \[timeout -k 10 10\] -s --max-time 5 --unix-socket /run/minerbot-dashboard/dashboard.sock -H Host: localhost http://localhost/health/ready$' \
        '^curl \[timeout -k 10 15\] -s -o /dev/null -w %\{http_code\} --max-time 10 https://bot.murderminershub.com/$' \
        '^docker \[timeout -k 10 120\] compose -f /opt/minerbot/compose.yml --project-directory /opt/minerbot up -d --no-build --no-deps bot$' \
        '^docker \[timeout -k 10 30\] logs --since \S+ minerbot-bot$'
    dash=$(image_of "$c5" dashboard)
    bot=$(image_of "$c5" bot)
    [ "$(ctr_image minerbot-dashboard)" = "$dash" ] || problem "the dashboard doesn't run c5's image"
    [ "$(ctr_image minerbot-bot)" = "$bot" ] || problem "the bot doesn't run c5's image"
    [ "$(tag_of minerbot-dashboard:current)" = "$dash" ] && [ "$(tag_of minerbot-dashboard:previous)" = "$(image_of "$c1" dashboard)" ] \
        || problem "the dashboard's current and previous tags aren't c5's and c1's images"
    [ "$(tag_of minerbot-bot:current)" = "$bot" ] && [ "$(tag_of minerbot-bot:previous)" = "$(image_of "$c1" bot)" ] \
        || problem "the bot's current and previous tags aren't c5's and c1's images"
    expect_said "The dashboard is ready: /health/ready answers Healthy through its socket, which only it can make." "the full deploy" "$log"
    expect_said "The public home page answers 200, as before the swap." "the full deploy" "$log"
    expect_said "The bot is ready, and still runs." "the full deploy" "$log"
    grep -qP "\tdeploy\tdashboard\t${c5:0:12}\tok\twas ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the dashboard's swap"
    grep -qP "\tdeploy\tbot\t${c5:0:12}\tok\twas ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the bot's swap"
    tail -n 1 "$home/logs/history.log" | grep -qP "\tdeploy\tdashboard,bot\t${c5:0:12}\tok" || problem "the run's closing history line isn't ok"
    cmp -s "$home/compose.yml" "$home/state/compose-ready-dashboard.yml" && cmp -s "$home/compose.yml" "$home/state/compose-ready-bot.yml" \
        || problem "no copy of the compose.yml each service is ready under, in state/"
    [ "$(find "$home/logs" -maxdepth 1 -name '[0-9]*Z-*.log' | wc -l)" = 60 ] && [ -e "$log" ] \
        && [ ! -e "$home/logs/20260101T000010Z-deploy.log" ] || problem "the run logs weren't pruned to the newest 60"
    verdict
}

case_5() {
    begin 5 "the same commit again: both services are already on it, and no tag moves, :previous included; restarts from before the deploy don't count, whether Compose leaves the container running or starts it, and Docker not answering about the count stops the deploy instead of counting 0"
    local before what="the same commit again after earlier restarts"
    restore deployed
    before=$(fingerprint /fake/docker/tags/minerbot-*@current /fake/docker/tags/minerbot-*@previous /fake/docker/ctr)
    in_home again 120 ./deploy.sh
    expect_exit 0 "the same commit again"
    expect_said "The dashboard is already on ${c1:0:12}." "the same commit again"
    expect_said "The bot is already on ${c1:0:12}." "the same commit again"
    [ "$(grep -cP "\tdeploy\t(dashboard|bot)\t${c1:0:12}\tok\talready on it" "$home/logs/history.log")" = 2 ] \
        || problem "history.log hasn't two 'already on it' lines"
    ! run_calls | grep -qE '^docker \[[^]]*\] tag ' || problem "a tag moved" "$(run_calls | args_of | grep ' tag ')"
    same_state "current, previous or a container changed" "$before" \
        "$(fingerprint /fake/docker/tags/minerbot-*@current /fake/docker/tags/minerbot-*@previous /fake/docker/ctr)"

    # Restarts from before this deploy: the running bot keeps its count of 2, and the stopped dashboard, which Compose
    # starts, begins again at 0, as Docker sets the count back on a start. Neither is a restart during the deploy.
    restore deployed
    awk '{ $3 = 2; print }' /fake/docker/ctr/minerbot-bot > /tmp/mbtest-ctr && mv /tmp/mbtest-ctr /fake/docker/ctr/minerbot-bot
    awk '{ $2 = "false"; $3 = 3; $6 = 0; print }' /fake/docker/ctr/minerbot-dashboard > /tmp/mbtest-ctr \
        && mv /tmp/mbtest-ctr /fake/docker/ctr/minerbot-dashboard
    in_home restarted 120 ./deploy.sh
    expect_exit 0 "$what"
    expect_said "The dashboard is already on ${c1:0:12}." "$what"
    expect_said "The dashboard is ready: /health/ready answers Healthy through its socket, which only it can make." "$what"
    expect_said "The bot is ready, and still runs." "$what"
    ! said "its container restarted" || problem "$what: a restart from before the deploy counted as one" "$(excerpt)"
    [ "$(cut -d' ' -f2,3 /fake/docker/ctr/minerbot-dashboard)" = "true 0" ] || problem "$what: the dashboard wasn't started"

    # Docker doesn't answer the one look at the bot's restart count, as at its 30 seconds under load: the deploy stops
    # on that, and never takes the restarts from before for one during the deploy
    what="the same commit again while Docker doesn't answer about the bot's restarts"
    restore deployed
    awk '{ $3 = 2; print }' /fake/docker/ctr/minerbot-bot > /tmp/mbtest-ctr && mv /tmp/mbtest-ctr /fake/docker/ctr/minerbot-bot
    trap 'if [ -e /usr/local/bin/docker.mbtest-5 ]; then mv -f /usr/local/bin/docker.mbtest-5 /usr/local/bin/docker; fi' EXIT
    if mv /usr/local/bin/docker /usr/local/bin/docker.mbtest-5; then
        cat > /usr/local/bin/docker << 'EOF'
#!/usr/bin/env bash
if [ "${1:-} ${2:-} ${3:-} ${4:-} ${5:-}" = "container inspect -f {{.RestartCount}} minerbot-bot" ]; then
    echo "Error response from daemon: a made-up timeout" >&2
    exit 124
fi
exec /usr/local/bin/docker.mbtest-5 "$@"
EOF
        chmod 755 /usr/local/bin/docker
        in_home restarts-unknown 120 ./deploy.sh --only bot
        mv -f /usr/local/bin/docker.mbtest-5 /usr/local/bin/docker
        expect_exit 1 "$what"
        expect_said "Docker didn't answer about the container minerbot-bot (above). Nothing more was changed." "$what"
        ! said "its container restarted" || problem "$what: a restart from before the deploy counted as one" "$(excerpt)"
    else
        problem "the test couldn't set the stand-in docker aside"
    fi
    verdict
}

case_6() {
    begin 6 "the dashboard not ready (it never makes its socket): back on its exact image, :failed set, :previous as before (or still none), its log saved 600 and never shown, the bot untouched, RESULT failed"
    local bot_before failed log
    restore deployed
    set_main "$c5"
    bot_before=$(fingerprint /fake/docker/ctr/minerbot-bot /fake/docker/tags/minerbot-bot@current /fake/docker/tags/minerbot-bot@previous)
    in_home unready 120 env FAKE_NO_SOCKET="dashboard@${c5:0:12}" ./deploy.sh
    expect_exit 1 "the unready dashboard"
    log=$(last_log deploy)
    expect_said "its socket /run/minerbot-dashboard/dashboard.sock isn't there" "the unready dashboard" "$log"
    expect_said "so it's back on ${c1:0:12}" "the unready dashboard" "$log"
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$c1" dashboard)" ] || problem "the dashboard isn't back on c1's image"
    [ "$(tag_of minerbot-dashboard:current)" = "$(image_of "$c1" dashboard)" ] || problem ":current isn't c1's image again"
    [ "$(tag_of minerbot-dashboard:failed)" = "$(image_of "$c5" dashboard)" ] || problem ":failed isn't c5's image"
    [ "$(tag_of minerbot-dashboard:previous)" = "$old_dashboard" ] || problem ":previous isn't the image it was before the deploy"
    failed=$(find "$home/logs" -name 'failed-dashboard-*.log' | head -n 1)
    if [ -z "$failed" ]; then
        problem "no logs/failed-dashboard-*.log"
    else
        [ "$(stat -c %a "$failed")" = 600 ] || problem "$failed isn't 600"
        said "$steam_code" "$failed" || problem "$failed doesn't hold the container's last lines"
    fi
    ! said "$steam_code" || problem "the container's log was printed"
    ! said "$steam_code" "$log" || problem "the container's log is in the run log"
    same_state "the bot was touched" "$bot_before" \
        "$(fingerprint /fake/docker/ctr/minerbot-bot /fake/docker/tags/minerbot-bot@current /fake/docker/tags/minerbot-bot@previous)"
    ! run_calls | args_of | grep -qE ' up -d --no-build --no-deps( --force-recreate)? bot$' || problem "the bot was swapped"
    grep -qx 'RESULT failed' "$log" || problem "the run log has no RESULT failed"
    grep -qP "\tdeploy\tdashboard\t${c5:0:12}\tnot ready\tback on ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the failed swap"

    # Without a :previous before the deploy, there's none after it either
    restore deployed
    set_main "$c5"
    rm -f /fake/docker/tags/minerbot-dashboard@previous
    in_home unready-no-previous 120 env FAKE_NO_SOCKET="dashboard@${c5:0:12}" ./deploy.sh --only dashboard
    expect_exit 1 "the unready dashboard without a :previous"
    [ "$(tag_of minerbot-dashboard:current)" = "$(image_of "$c1" dashboard)" ] || problem "without a :previous, :current isn't c1's image again"
    [ -z "$(tag_of minerbot-dashboard:previous)" ] || problem "without a :previous before, the failed swap left one"
    verdict
}

case_7() {
    begin 7 "the public check fails while localhost is fine: the dashboard goes back to its earlier image"
    local log
    restore deployed
    set_main "$c5"
    in_home public 120 env FAKE_PUBLIC_FAIL="dashboard@${c5:0:12}" ./deploy.sh --only dashboard
    expect_exit 1 "the failed public check"
    log=$(last_log deploy)
    expect_said "answered 200 before the swap but not within" "the failed public check" "$log"
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$c1" dashboard)" ] || problem "the dashboard isn't back on c1's image"
    [ "$(tag_of minerbot-dashboard:failed)" = "$(image_of "$c5" dashboard)" ] || problem ":failed isn't c5's image"
    verdict
}

case_8() {
    begin 8 "the bot not ready (no ready line in its log): the bot goes back to its earlier image, and the new dashboard stays"
    local log
    restore deployed
    set_main "$c5"
    in_home bot 120 env FAKE_UNREADY="bot@${c5:0:12}" ./deploy.sh
    expect_exit 1 "the unready bot"
    log=$(last_log deploy)
    expect_said "The bot wasn't ready within" "the unready bot" "$log"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c1" bot)" ] || problem "the bot isn't back on c1's image"
    [ "$(tag_of minerbot-bot:failed)" = "$(image_of "$c5" bot)" ] || problem "the bot's :failed isn't c5's image"
    [ "$(tag_of minerbot-bot:previous)" = "$old_bot" ] || problem "the bot's :previous isn't what it was"
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$c5" dashboard)" ] || problem "the new dashboard didn't stay"
    grep -qP "\tdeploy\tbot\t${c5:0:12}\tnot ready\tback on ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the bot"
    verdict
}

case_9() {
    begin 9 "the first dashboard swap not ready: the dashboard is stopped and removed, :current taken away and :failed set; and when Compose can't stop it, the deploy says it's left running instead"
    local log
    restore setup
    in_home first 120 env FAKE_UNREADY="dashboard@${c1:0:12}" ./deploy.sh --only dashboard
    expect_exit 1 "the first unready swap"
    log=$(last_log deploy)
    expect_said "there's no earlier image to go back to, so it's stopped and removed" "the first unready swap" "$log"
    [ ! -e /fake/docker/ctr/minerbot-dashboard ] || problem "the dashboard's container is still there"
    [ -z "$(tag_of minerbot-dashboard:current)" ] || problem "minerbot-dashboard:current is still there"
    [ "$(tag_of minerbot-dashboard:failed)" = "$(image_of "$c1" dashboard)" ] || problem ":failed isn't the image that wasn't ready"
    in_order "stopped, then removed" ' compose .* stop dashboard$' ' compose .* rm -f dashboard$'

    # Compose can't stop it: its stop fails, and its rm -f, like the real one's without -s, leaves a running container
    # where it is ("No stopped containers"). The deploy looks before it says the dashboard is stopped and removed.
    restore setup
    trap 'if [ -e /usr/local/bin/docker.mbtest-9 ]; then mv -f /usr/local/bin/docker.mbtest-9 /usr/local/bin/docker; fi' EXIT
    if mv /usr/local/bin/docker /usr/local/bin/docker.mbtest-9; then
        cat > /usr/local/bin/docker << 'EOF'
#!/usr/bin/env bash
if [ "${1:-} ${2:-}" = "compose -f" ] && [[ " $* " == *" rm -f dashboard "* ]]; then
    read -r _ running _ < /fake/docker/ctr/minerbot-dashboard 2> /dev/null || running=''
    if [ "$running" = true ]; then
        echo "No stopped containers" >&2
        exit 0
    fi
fi
exec /usr/local/bin/docker.mbtest-9 "$@"
EOF
        chmod 755 /usr/local/bin/docker
        in_home first-not-stopped 120 env FAKE_UNREADY="dashboard@${c1:0:12}" FAKE_STOP=fail ./deploy.sh --only dashboard
        mv -f /usr/local/bin/docker.mbtest-9 /usr/local/bin/docker
        expect_exit 1 "the first unready swap that Compose couldn't stop"
        log=$(last_log deploy)
        expect_said "there's no earlier image to go back to, but Compose couldn't stop and remove it (above), so it's left on ${c1:0:12}. Stop and remove it with: docker compose rm -s -f dashboard." \
            "the first unready swap that Compose couldn't stop" "$log"
        ! said "so it's stopped and removed" "$log" \
            || problem "the deploy said the dashboard is stopped and removed while its container still runs"
        [ -e /fake/docker/ctr/minerbot-dashboard ] || problem "the test's start: the dashboard's container is gone"
        grep -qP "\tdeploy\tdashboard\t${c1:0:12}\tnot ready\tnot stopped, with no earlier image(\t|$)" "$home/logs/history.log" \
            || problem "no history line that says the dashboard wasn't stopped" "$(tail -n 2 "$home/logs/history.log")"
    else
        problem "the test couldn't set the stand-in docker aside"
    fi
    verdict
}

case_10() {
    begin 10 "failures that stop before any swap: a hanging build, a failing migration, a failing dump, a failing rehearsal, a difference in the rights, failing settings; and a migrate container left behind is removed first"
    local before log
    sub() {   # sub <name> <main> <text> <NAME=value>...: a deploy of the dashboard that must stop before the swap
        local name=$1 commit=$2 text=$3
        shift 3
        restore deployed
        set_main "$commit"
        before=$(docker_state)
        in_home "$name" 120 env "$@" ./deploy.sh --only dashboard
        expect_exit 1 "$name"
        expect_said "$text" "$name"
        ! run_calls | args_of | grep -qE ' compose .* (up|stop|rm) ' || problem "$name: a service was touched"
        same_state "$name: a container or a current/previous tag changed" "$(printf '%s\n' "$before" | grep -v '@new$')" \
            "$(docker_state | grep -v '@new$')"
    }
    sub hang "$c5" "Building the dashboard failed or took more than 20 minutes" FAKE_BUILD=hang-dashboard
    run_calls | args_of | grep -qE '^docker \[timeout -k 30 1200\] build ' || problem "the build didn't run under timeout -k 30 1200"
    sub migrate "$c6" "The migrations didn't finish (above)." FAKE_MIGRATE=fail
    in_order "docker rm -f after the failed migration" ' run .* migrate$' '^docker \[timeout -k 10 30\] rm -f -v minerbot-migrate$'
    [ "$(cat /fake/db/version)" = 1 ] || problem "the failed migration changed the version"
    sub dump "$c6" "The dump before the migrations failed (above), so nothing was migrated" FAKE_DUMP=fail
    ! run_calls | args_of | grep -qE ' run .* migrate$' || problem "it migrated after a failed dump"
    sub rehearsal "$c5" "The rehearsal of dashboard-rights.sql on a throwaway login failed" FAKE_REHEARSAL=fail
    ! run_calls | grep -qF '{stdin: apply}' || problem "the real login was applied after a failed rehearsal"
    [ ! -e /fake/db/check-login ] || problem "the throwaway login is still there"
    sub rights "$c5" "doesn't have exactly the rights the file gives" FAKE_RIGHTS_EXTRA=1
    expect_said "> proxy someone localhost" "the difference in the rights"
    sub settings "$c5" "The new dashboard needs settings dashboard.env doesn't have" FAKE_CHECK_SETTINGS=fail
    ! run_calls | grep -qF 'ACCOUNT LOCK' || problem "the rights ran after the settings failed"

    restore deployed
    set_main "$c6"
    echo "sha256:0 true 0 2026-11-05T00:00:00Z none 4321 deadbeef none" > /fake/docker/ctr/minerbot-migrate
    in_home left-behind 120 ./deploy.sh --only dashboard --no-start
    expect_exit 0 "the migration with a container left behind"
    in_order "the left-behind container removed before the migration" '^docker \[timeout -k 10 30\] rm -f -v minerbot-migrate$' \
        ' run --rm --pull never --name minerbot-migrate '
    [ "$(cat /fake/db/version)" = 2 ] || problem "the migration didn't run after the left-behind container was removed"

    # A settings check container left behind is removed first too; and when dropping the rehearsal's throwaway login fails
    # once, the run's end drops it
    restore deployed
    set_main "$c5"
    echo "sha256:0 true 0 2026-11-05T00:00:00Z none 4321 deadbeef none" > /fake/docker/ctr/minerbot-dashboard-check
    in_home check-left-behind 120 env FAKE_DROP_CHECK=fail-once ./deploy.sh --only dashboard
    expect_exit 0 "the deploy with a check container left behind and a failed drop"
    in_order "the left-behind check container removed before the check" \
        '^docker \[timeout -k 10 30\] rm -f -v minerbot-dashboard-check$' ' run --rm --pull never --name minerbot-dashboard-check '
    expect_said "Couldn't drop the throwaway login minerbot-rights-check" "the deploy with a failed drop" "$(last_log deploy)"
    [ ! -e /fake/db/check-login ] || problem "the throwaway login is still there after the run ended"
    verdict
}

case_11() {
    begin 11 "time: a run killed at its limit ends without a result; a late run stops before the dashboard's swap with the tags as before; with time for the swaps but not the tidy, the tidy is skipped and the run ends ok"
    local before log
    restore deployed
    set_main "$c5"
    in_home killed 90 env FAKE_UNIT_LIMIT=3 FAKE_BUILD=slow ./deploy.sh --only dashboard
    expect_exit 1 "the killed run"
    expect_said "It ended without a result: it was stopped at its time limit, or the server restarted." "the killed run"
    flock -n "$home/state/lock" true || problem "the lock is still held after the unit was killed"
    ! pgrep -f '/opt/minerbot/deploy.sh' > /dev/null || problem "a process of the killed unit is still running"

    restore deployed
    set_main "$c5"
    before=$(fingerprint /fake/docker/tags/minerbot-*@current /fake/docker/tags/minerbot-*@previous /fake/docker/ctr)
    direct late 120 MINERBOT_DEADLINE="$(($(date +%s) + 500))" -- ./deploy.sh
    expect_exit 1 "the late run"
    expect_said "There isn't enough time left in this run for a safe swap of the dashboard. Nothing was swapped." "the late run"
    same_state "the late run moved a tag or touched a container" "$before" \
        "$(fingerprint /fake/docker/tags/minerbot-*@current /fake/docker/tags/minerbot-*@previous /fake/docker/ctr)"

    restore deployed
    set_main "$c5"
    direct no-tidy 120 MINERBOT_DEADLINE="$(($(date +%s) + 1000))" FAKE_CLOCK_JUMP_AT_BOT_UP=700 -- ./deploy.sh
    rm -rf /fake/clock
    expect_exit 0 "the run without time for the tidy"
    expect_said "The tidy-up was skipped: there wasn't enough time left in this run." "the run without time for the tidy"
    expect_said "RESULT ok" "the run without time for the tidy"
    ! run_calls | args_of | grep -qE ' image prune | builder prune ' || problem "it tidied up without the time for it"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c5" bot)" ] || problem "the bot wasn't swapped"
    verdict
}

case_12() {
    begin 12 "rollback.sh toggles by image ID: back, then forward again; it refuses when there's nothing to go back to, :previous missing or the image it runs, and before its unit when the lock is held; it prints the usage without a service"
    local c1_bot before
    restore deployed
    c1_bot=$(image_of "$c1" bot)
    in_home back 60 ./rollback.sh bot
    expect_exit 0 "the rollback"
    [ "$(ctr_image minerbot-bot)" = "$old_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$old_bot" ] \
        && [ "$(tag_of minerbot-bot:previous)" = "$c1_bot" ] || problem "the rollback didn't swap the bot's images by ID" "$(excerpt)"
    grep -qP "\trollback\tbot\timage 000000000000\tok\twas ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the rollback"
    in_home forward 60 ./rollback.sh bot
    expect_exit 0 "the second rollback"
    [ "$(ctr_image minerbot-bot)" = "$c1_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$c1_bot" ] \
        && [ "$(tag_of minerbot-bot:previous)" = "$old_bot" ] || problem "the second rollback didn't go forward again" "$(excerpt)"
    rm -f /fake/docker/tags/minerbot-dashboard@previous
    before=$(docker_state)
    in_home nothing 60 ./rollback.sh dashboard
    expect_exit 1 "the rollback with nothing to go back to"
    expect_said "There's nothing to go back to" "the rollback with nothing to go back to"
    same_state "the refused rollback changed something" "$before" "$(docker_state)"
    in_home usage 30 ./rollback.sh
    expect_exit 1 "rollback.sh without a service"
    expect_said "Usage: ./rollback.sh bot|dashboard" "rollback.sh without a service"

    # :previous is the image the bot runs: nothing to go back to either
    restore deployed
    cp /fake/docker/tags/minerbot-bot@current /fake/docker/tags/minerbot-bot@previous
    before=$(docker_state)
    in_home same 60 ./rollback.sh bot
    expect_exit 1 "the rollback to the image the bot runs"
    expect_said "There's nothing to go back to" "the rollback to the image the bot runs"
    same_state "the rollback to the image the bot runs changed something" "$before" "$(docker_state)"

    # A held lock: refused before the unit starts
    restore deployed
    hold_lock "$home/state/lock"
    in_home locked 60 ./rollback.sh bot
    let_go
    expect_exit 1 "the rollback while the lock is held"
    expect_said "Another run holds the lock" "the rollback while the lock is held"
    ! run_calls | grep -q '^systemd-run ' || problem "the rollback started its unit while the lock was held"
    verdict
}

# new_commit <base commit> <name> <command...>: a commit on top of <base>, made by the command run in /work/repo, and
# pushed to GitHub's stand-in as the branch mbtest-<name>; prints the commit
new_commit() {
    local base=$1 name=$2 made
    shift 2
    G -C /work/repo checkout -q --detach "$base" || return 1
    (cd /work/repo && "$@") || return 1
    made=$(commit_all "$name") || return 1
    G -C /work/repo push -q /work/origin.git "$made:refs/heads/mbtest-$name" || return 1
    printf '%s\n' "$made"
}

case_24() {
    begin 24 "refs by their full names: a tag named origin/main on GitHub never stands in for main, before or after it's deleted there, and waves nothing off main through; a tag moved on GitHub deploys at its new place, and main still deploys; a tag deleted there isn't deployed; a short commit ID works"
    local log
    restore built
    set_main "$c1"
    # Someone who can push tags pushes refs/tags/origin/main at c2, which isn't on main
    G -C /work/origin.git update-ref refs/tags/origin/main "$c2"
    in_home tagged 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy of main with a tag named origin/main on GitHub"
    log=$(last_log deploy)
    expect_said "The commit is ${c1:0:12} (main)." "the deploy of main with a tag named origin/main on GitHub" "$log"
    ! said "C2 code:" "$log" || problem "c2's deploy.sh ran"
    [ "$(cat "$home/state/deploy-files.commit" 2> /dev/null)" = "$c1" ] || problem "the installed deploy files aren't c1's"
    ! run_calls | args_of | grep -qF "revision=$c2 " || problem "it built c2, the tag's commit"
    in_home c2-by-name 120 ./deploy.sh c2 --only dashboard --no-start
    expect_exit 1 "c2 by its branch, with the tag on GitHub"
    expect_said "isn't one of main's own commits" "c2 by its branch, with the tag on GitHub"
    in_home tag-by-name 120 ./deploy.sh origin/main --only dashboard --no-start
    expect_exit 1 "the tag by its own name"
    expect_said "${c2:0:12} (origin/main) isn't one of main's own commits" "the tag by its own name"
    # The tag deleted on GitHub: src loses it too
    G -C /work/origin.git update-ref -d refs/tags/origin/main
    in_home after-delete 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy of main after the tag was deleted"
    ! G -C "$home/src" rev-parse -q --verify refs/tags/origin/main > /dev/null || problem "src kept the tag deleted on GitHub"

    # A tag moved on GitHub after src fetched it, then deleted there
    restore built
    G -C /work/origin.git tag v1 "$c1"
    in_home tag-first 120 ./deploy.sh v1 --only bot --no-start
    expect_exit 0 "the first deploy of v1"
    set_main "$c5"
    G -C /work/origin.git tag -f v1 "$c5" > /dev/null
    in_home main-after-move 120 ./deploy.sh --only bot --no-start
    expect_exit 0 "the deploy of main after v1 moved on GitHub"
    expect_said "The commit is ${c5:0:12} (main)." "the deploy of main after v1 moved on GitHub" "$(last_log deploy)"
    in_home tag-after-move 120 ./deploy.sh v1 --only bot --no-start
    expect_exit 0 "the deploy of v1 after it moved"
    expect_said "The commit is ${c5:0:12} (v1)." "the deploy of v1 after it moved" "$(last_log deploy)"
    G -C /work/origin.git tag -d v1 > /dev/null
    in_home tag-deleted 120 ./deploy.sh v1 --only bot --no-start
    expect_exit 1 "the deploy of v1 after it was deleted on GitHub"
    expect_said "There's no branch, tag or commit called v1 on GitHub." "the deploy of v1 after it was deleted on GitHub"
    in_home short-id 120 ./deploy.sh "${c5:0:7}" --only bot --no-start
    expect_exit 0 "the deploy of a short commit ID"
    expect_said "The commit is ${c5:0:12} (${c5:0:7})." "the deploy of a short commit ID" "$(last_log deploy)"
    verdict
}

case_25() {
    begin 25 "only commits main itself pointed at deploy: a pull request merged with a merge commit is refused by its first commit, which changes deploy.sh, and by its tip, with nothing installed or built; the merge, by name or ID, and an older commit of main deploy"
    local p1 p2 merge before
    restore built
    p1=$(new_commit "$c1" pr-one sh -c 'echo "# P1 (made up): never reviewed on its own" >> deploy/deploy.sh') || problem "couldn't make p1"
    p2=$(new_commit "$p1" pr-two sh -c "/usr/bin/git checkout -q $c1 -- deploy/deploy.sh && echo notes > notes.txt") \
        || problem "couldn't make p2"
    { G -C /work/repo checkout -q --detach "$c1" && G -C /work/repo merge -q --no-ff -m "Merge pull request 99 (made up)" "$p2" \
        && merge=$(G -C /work/repo rev-parse HEAD) && G -C /work/repo push -q /work/origin.git "$merge:refs/heads/mbtest-merge"; } \
        || problem "couldn't make the merge"
    set_main "$merge"
    before=$(home_state)
    in_home inner 120 ./deploy.sh "$p1" --only bot --no-start
    expect_exit 1 "the pull request's first commit"
    expect_said "${p1:0:12} (${p1}) isn't one of main's own commits" "the pull request's first commit"
    ! run_calls | grep -qE '^docker \[[^]]*\] build ' || problem "the pull request's first commit was built"
    in_home tip 120 ./deploy.sh "${p2:0:9}" --only bot --no-start
    expect_exit 1 "the pull request's tip"
    expect_said "isn't one of main's own commits" "the pull request's tip"
    ! run_calls | grep -qE '^docker \[[^]]*\] build ' || problem "the pull request's tip was built"
    same_state "a refused commit of the pull request installed something" "$before" "$(home_state)"
    in_home merge-by-id 120 ./deploy.sh "$merge" --only bot --no-start
    expect_exit 0 "the merge by its ID"
    in_home main 120 ./deploy.sh --only bot --no-start
    expect_exit 0 "main, which is the merge"
    expect_said "The commit is ${merge:0:12} (main)." "main, which is the merge" "$(last_log deploy)"
    in_home older 120 ./deploy.sh "$c1" --only bot --no-start
    expect_exit 0 "an older commit of main"
    expect_said "Kept the deploy files of ${merge:0:12}, which are newer than ${c1:0:12}." "an older commit of main" "$(last_log deploy)"
    verdict
}

case_26() {
    begin 26 "Compose can't recreate a service: the deploy stops with its own message, RESULT failed and no ok in history, the containers, :current and :previous as before; the same when Compose exits 0 and leaves the container; the already-on-it path says so; a rollback Compose can't do puts its tags back, and the next one works"
    local log before
    restore deployed
    set_main "$c5"
    before=$(fingerprint /fake/docker/ctr /fake/docker/tags/minerbot-*@current /fake/docker/tags/minerbot-*@previous)
    in_home up-fails 120 env FAKE_UP_FAIL="dashboard bot" ./deploy.sh
    expect_exit 1 "the deploy Compose can't do"
    log=$(last_log deploy)
    expect_said "Compose couldn't recreate the dashboard on ${c5:0:12} (above), so it's back on ${c1:0:12}, which is ready." \
        "the deploy Compose can't do" "$log"
    grep -qx 'RESULT failed' "$log" 2> /dev/null || problem "the deploy Compose can't do has no RESULT failed"
    same_state "the deploy Compose can't do changed a container, :current or :previous" "$before" \
        "$(fingerprint /fake/docker/ctr /fake/docker/tags/minerbot-*@current /fake/docker/tags/minerbot-*@previous)"
    [ "$(tag_of minerbot-dashboard:failed)" = "$(image_of "$c5" dashboard)" ] || problem ":failed isn't c5's dashboard"
    grep -qP "\tdeploy\tdashboard\t${c5:0:12}\tcompose failed\tback on ${c1:0:12}" "$home/logs/history.log" \
        || problem "no 'compose failed' history line" "$(tail -n 3 "$home/logs/history.log")"
    ! grep -qP "\t${c5:0:12}\tok" "$home/logs/history.log" || problem "history.log says ok"

    # Compose exits 0 but leaves the bot as it was
    restore deployed
    set_main "$c5"
    in_home up-keeps 120 env FAKE_UP_KEEP=bot ./deploy.sh --only bot
    expect_exit 1 "the deploy where Compose leaves the bot as it was"
    log=$(last_log deploy)
    expect_said "The bot isn't ready: its container runs ${c1:0:12}, not ${c5:0:12}, so Compose didn't recreate it." \
        "the deploy where Compose leaves the bot as it was" "$log"
    expect_said "so it's back on ${c1:0:12}, which is ready." "the deploy where Compose leaves the bot as it was" "$log"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c1" bot)" ] && [ "$(tag_of minerbot-bot:current)" = "$(image_of "$c1" bot)" ] \
        && [ "$(tag_of minerbot-bot:previous)" = "$old_bot" ] || problem "the bot's container or tags aren't as before the deploy"

    # The same commit again, Compose failing
    restore deployed
    in_home already-fails 120 env FAKE_UP_FAIL=bot ./deploy.sh --only bot
    expect_exit 1 "the same commit again, Compose failing"
    expect_said "The bot is already on ${c1:0:12}, but Compose couldn't start or recreate it (above)." \
        "the same commit again, Compose failing" "$(last_log deploy)"
    ! grep -qP "\tdeploy\tbot\t${c1:0:12}\tok\talready on it" "$home/logs/history.log" || problem "history.log says ok, already on it"

    # A rollback Compose can't do, then one it can
    restore deployed
    before=$(docker_state)
    in_home rollback-fails 60 env FAKE_UP_FAIL=bot ./rollback.sh bot
    expect_exit 1 "the rollback Compose can't do"
    expect_said "Compose didn't recreate the bot on image 000000000000 (above), so it still runs ${c1:0:12}, and its tags are back as they were. Nothing was changed." \
        "the rollback Compose can't do" "$(last_log rollback)"
    same_state "the rollback Compose can't do changed a container or a tag" "$before" "$(docker_state)"
    in_home rollback-again 60 ./rollback.sh bot
    expect_exit 0 "the rollback once Compose works again"
    [ "$(ctr_image minerbot-bot)" = "$old_bot" ] || problem "the rollback once Compose works again didn't go back"
    verdict
}

case_27() {
    begin 27 "a compose.yml change the bot can't run under: the swap puts back the compose.yml the bot was last ready under, goes back to its image, says so in the log and history, and the next deploy installs the new file again; the already-on-it path does the same; an image that isn't ready puts nothing back"
    local bad log
    restore deployed
    cp "$home/compose.yml" "$home/state/compose-ready-bot.yml"
    cp "$home/compose.yml" "$home/state/compose-ready-dashboard.yml"
    bad=$(new_commit "$c5" bot-never-ready sed -i 's/^    pids_limit: 512$/    pids_limit: 512\n    labels:\n      - mbtest-never-ready=1/' deploy/compose.yml) \
        || problem "couldn't make the commit with the bot's new setting"
    G -C /work/origin.git show "$bad:deploy/compose.yml" | grep -q mbtest-never-ready || problem "the test's commit didn't change the bot's section"
    set_main "$bad"
    in_home bad-compose 120 ./deploy.sh --only bot
    expect_exit 1 "the deploy of a compose.yml the bot can't run under"
    log=$(last_log deploy)
    expect_said "compose.yml changed the bot's settings since it was last ready, so the compose.yml it was ready under is back in place" \
        "the deploy of a compose.yml the bot can't run under" "$log"
    expect_said "so it's back on ${c1:0:12}, which is ready." "the deploy of a compose.yml the bot can't run under" "$log"
    cmp -s "$home/compose.yml" "$home/state/compose-ready-bot.yml" && ! grep -q mbtest-never-ready "$home/compose.yml" \
        || problem "the compose.yml the bot was last ready under isn't back"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c1" bot)" ] || problem "the bot isn't back on c1's image"
    grep -qP "\tdeploy\tbot\t${bad:0:12}\tnot ready\tback on ${c1:0:12}, under the earlier compose.yml" "$home/logs/history.log" \
        || problem "no history line that names the earlier compose.yml" "$(tail -n 3 "$home/logs/history.log")"
    in_home again 120 ./deploy.sh --only bot
    expect_exit 1 "the next deploy of the same commit"
    log=$(last_log deploy)
    expect_said "Installed the deploy files of ${bad:0:12} that changed: compose.yml." "the next deploy of the same commit" "$log"
    expect_said "the compose.yml it was ready under is back in place" "the next deploy of the same commit" "$log"

    # The already-on-it path: the bot on c5, under a compose.yml since replaced by one it can't run under
    restore deployed
    set_main "$c5"
    in_home bot-on-c5 120 ./deploy.sh --only bot
    expect_exit 0 "the bot's deploy of c5"
    bad=$(new_commit "$c5" bot-never-ready-2 sed -i 's/^    pids_limit: 512$/    pids_limit: 512\n    labels:\n      - mbtest-never-ready=1/' deploy/compose.yml) \
        || problem "couldn't make the second commit with the bot's new setting"
    set_main "$bad"
    in_home dashboard-only 120 ./deploy.sh --only dashboard
    expect_exit 0 "the dashboard's deploy that installs the bot's new setting"
    in_home already-on-it 120 ./deploy.sh "$c5" --only bot
    expect_exit 1 "the bot already on c5, with the new setting installed"
    log=$(last_log deploy)
    expect_said "The bot is already on ${c5:0:12}, but it isn't ready (above). compose.yml changed the bot's settings since it was last ready" \
        "the bot already on c5, with the new setting installed" "$log"
    expect_said "It's ready again with that one." "the bot already on c5, with the new setting installed" "$log"
    ! grep -q mbtest-never-ready "$home/compose.yml" || problem "the already-on-it path didn't put the earlier compose.yml back"

    # An image that isn't ready, with compose.yml as the bot was last ready under: nothing is put back
    restore deployed
    cp "$home/compose.yml" "$home/state/compose-ready-bot.yml"
    set_main "$c5"
    in_home image-unready 120 env FAKE_UNREADY="bot@${c5:0:12}" ./deploy.sh --only bot
    expect_exit 1 "the deploy of a bot image that isn't ready"
    ! said "is back in place" "$(last_log deploy)" || problem "compose.yml was put back though it didn't change the bot's settings"
    verdict
}

case_28() {
    begin 28 "the dashboard's socket: common.sh, compose.yml and the tmpfiles line name the same folder and socket, bind-mounted without being made; the deploy puts a wrong folder right; socket_problem finds a wrong folder, a socket that isn't the dashboard's or one nginx can't write to; a rollback onto a wrong folder isn't ready; a failing systemd-tmpfiles stops the deploy before any build"
    local names
    restore deployed
    # shellcheck source=/dev/null disable=SC2154 # common.sh, as the box has it, sets the three
    names=$(source /repo/deploy/common.sh && printf '%s %s %s' "$socket_dir" "$dashboard_socket" "$tmpfiles_conf")
    [ "$names" = "/run/minerbot-dashboard /run/minerbot-dashboard/dashboard.sock /etc/tmpfiles.d/minerbot-dashboard.conf" ] \
        || problem "common.sh's names for the socket aren't the designed ones" "$names"
    [ "$(grep -v '^#' /repo/deploy/minerbot-dashboard.tmpfiles | grep .)" = "d /run/minerbot-dashboard 2750 1654 nobody -" ] \
        || problem "minerbot-dashboard.tmpfiles isn't the one designed line"
    grep -qx '      Dashboard__UnixSocket: /run/minerbot-dashboard/dashboard.sock' /repo/deploy/compose.yml \
        || problem "compose.yml doesn't give the dashboard Dashboard__UnixSocket"
    [ "$(grep -A4 -x '      - type: bind' /repo/deploy/compose.yml)" = "$(printf '%s\n' '      - type: bind' \
        '        source: /run/minerbot-dashboard' '        target: /run/minerbot-dashboard' '        bind:' '          create_host_path: false')" ] \
        || problem "compose.yml doesn't bind-mount the folder at the same path without making it"

    # socket_problem, which the readiness check and status.sh use, on each way the socket can be wrong
    # shellcheck source=/dev/null
    sp() { (source /repo/deploy/common.sh && socket_problem); }
    # shellcheck source=/dev/null
    (source /usr/local/lib/mbtest/record.sh && sync_socket)
    [ -z "$(sp)" ] || problem "socket_problem finds something wrong with the dashboard's own socket" "$(sp)"
    chmod 600 /run/minerbot-dashboard/dashboard.sock
    [ "$(sp)" = "its socket's mode is 600, so nginx, in the group nobody, can't connect to it" ] || problem "a socket nginx can't write to passes" "$(sp)"
    chmod 660 /run/minerbot-dashboard/dashboard.sock
    chown 4001 /run/minerbot-dashboard/dashboard.sock
    [ "$(sp)" = "/run/minerbot-dashboard/dashboard.sock isn't a socket owned by 1654 and the group nobody: it's a socket of 4001 and nobody" ] \
        || problem "a socket of another owner passes" "$(sp)"
    rm -f /run/minerbot-dashboard/dashboard.sock
    [ "$(sp)" = "its socket /run/minerbot-dashboard/dashboard.sock isn't there" ] || problem "a missing socket passes" "$(sp)"
    # Five digits, as a shorter mode keeps a folder's setgid bit
    chmod 00755 /run/minerbot-dashboard
    [ "$(sp)" = "its socket's folder /run/minerbot-dashboard is 755 1654 nobody directory, not a folder with mode 2750, owner 1654 and group nobody, which systemd-tmpfiles --create /etc/tmpfiles.d/minerbot-dashboard.conf makes" ] \
        || problem "a folder with the wrong mode passes" "$(sp)"

    # rollback.sh doesn't make the folder, so the dashboard isn't ready on it
    in_home rollback-folder 60 ./rollback.sh dashboard
    expect_exit 1 "the rollback with the socket's folder 755"
    expect_said "its socket's folder /run/minerbot-dashboard is 755 1654 nobody directory" "the rollback with the socket's folder 755" \
        "$(last_log rollback)"

    # A deploy puts the folder right, and a systemd-tmpfiles that fails stops it before any build
    restore deployed
    chmod 00700 /run/minerbot-dashboard
    in_home folder-fixed 120 ./deploy.sh --only bot --no-start
    expect_exit 0 "the deploy with the socket's folder 700"
    [ "$(stat -c '%a %u %G' /run/minerbot-dashboard)" = "2750 1654 nobody" ] || problem "the deploy didn't put the socket's folder right"
    in_home tmpfiles-fails 120 env FAKE_TMPFILES=fail ./deploy.sh --only bot --no-start
    expect_exit 1 "the deploy where systemd-tmpfiles fails"
    expect_said "systemd-tmpfiles couldn't make /run/minerbot-dashboard, the folder of the dashboard's socket (above). Nothing was swapped." \
        "the deploy where systemd-tmpfiles fails"
    ! run_calls | grep -qE '^docker \[[^]]*\] build ' || problem "it built after systemd-tmpfiles failed"
    verdict
}

case_29() {
    begin 29 "after main's history is rewritten, the new main's deploy files replace the installed ones, whose commit is off main or gone from src, and say why; when main is set back to an older commit, the newer files stay"
    local rewritten log
    restore built
    { G -C /work/repo checkout -q --detach "$c1" && echo "# Rewritten history (made up)" >> /work/repo/deploy/common.sh \
        && G -C /work/repo commit -qa --amend -m "one, rewritten" && rewritten=$(G -C /work/repo rev-parse HEAD) \
        && G -C /work/repo push -q /work/origin.git "$rewritten:refs/heads/mbtest-rewritten"; } || problem "couldn't rewrite main"
    set_main "$rewritten"
    in_home rewritten 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy of the rewritten main"
    log=$(last_log deploy)
    expect_said "The installed deploy files are from ${c1:0:12}, which isn't on main any more: main's history was rewritten." \
        "the deploy of the rewritten main" "$log"
    [ "$(cat "$home/state/deploy-files.commit")" = "$rewritten" ] || problem "deploy-files.commit doesn't name the rewritten main"
    G -C /work/origin.git show "$rewritten:deploy/common.sh" | cmp -s - "$home/common.sh" || problem "the rewritten main's common.sh isn't installed"

    restore built
    echo 0123456789abcdef0123456789abcdef01234567 > "$home/state/deploy-files.commit"
    in_home gone 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy when the installed files' commit is gone from src"
    expect_said "The installed deploy files are from 0123456789ab, which src doesn't have any more: main's history was rewritten." \
        "the deploy when the installed files' commit is gone from src" "$(last_log deploy)"
    [ "$(cat "$home/state/deploy-files.commit")" = "$c1" ] || problem "deploy-files.commit doesn't name c1"

    restore built
    echo "$c2" > "$home/state/deploy-files.commit"
    in_home set-back 120 ./deploy.sh main --only dashboard --no-start
    expect_exit 0 "the deploy of main set back to c1"
    expect_said "Kept the deploy files of ${c2:0:12}, which are newer than ${c1:0:12}." "the deploy of main set back to c1" "$(last_log deploy)"
    verdict
}

case_30() {
    begin 30 "the deploy's and the rollback's units hold the lock while they run, and lock.info names them"
    local bg
    held_by() {   # held_by <what>: within 30 seconds, the lock is held and lock.info starts with <what>
        local n
        for n in $(seq 1 150); do
            if ! flock -n "$home/state/lock" true 2> /dev/null && grep -q "^$1, started " "$home/state/lock.info" 2> /dev/null; then
                return 0
            fi
            sleep 0.2
        done
        problem "$1: the lock wasn't held, with lock.info naming it, while its unit ran" "$(cat "$home/state/lock.info" 2> /dev/null)"
    }
    restore deployed
    set_main "$c5"
    rm -f "$home/state/lock.info"
    (in_home deploy-slow 150 env FAKE_BUILD=slow ./deploy.sh --only dashboard --no-start) &
    bg=$!
    held_by deploy
    wait "$bg"
    restore deployed
    rm -f "$home/state/lock.info"
    (in_home rollback-slow 90 env FAKE_UNREADY="bot@" ./rollback.sh bot) &
    bg=$!
    held_by "rollback bot"
    wait "$bg"
    verdict
}

case_31() {
    begin 31 "every unit gets its RuntimeMaxSec, TimeoutStopSec=30 and a MINERBOT_DEADLINE 30 seconds before its limit"
    local t0 t1
    unit_line() {   # unit_line <action> <limit> <time before> <time after>
        local line d
        line=$(run_calls | args_of | grep -E "^systemd-run \[timeout -k 10 30\] --quiet --collect --unit=minerbot-$1 " | head -n 1)
        [[ $line == *" -p RuntimeMaxSec=$2 -p TimeoutStopSec=30 "* ]] \
            || problem "$1: the unit's limits aren't RuntimeMaxSec=$2 and TimeoutStopSec=30" "$line"
        d=$(printf '%s\n' "$line" | sed -n 's/.*--setenv=MINERBOT_DEADLINE=\([0-9]*\) .*/\1/p')
        [ -n "$d" ] && [ "$d" -ge $(($3 + $2 - 30)) ] && [ "$d" -le $(($4 + $2 - 30)) ] \
            || problem "$1: MINERBOT_DEADLINE isn't 30 seconds before the unit's limit" "$line"
    }
    restore moved
    t0=$(date +%s)
    in_home deploy 120 ./deploy.sh main --only dashboard --no-start
    t1=$(date +%s)
    expect_exit 0 "the deploy"
    unit_line deploy 5400 "$t0" "$t1"
    restore deployed
    t0=$(date +%s)
    in_home rollback 60 ./rollback.sh bot
    t1=$(date +%s)
    expect_exit 0 "the rollback"
    unit_line rollback 900 "$t0" "$t1"
    verdict
}

case_32() {
    begin 32 "readiness: the dashboard not ready on the new image nor on the one before stops the deploy there, the bot untouched; a restart during the wait, Docker's unhealthy, and a bot that restarts right after its ready line each count as not ready"
    local log bot_before
    restore deployed
    set_main "$c5"
    bot_before=$(fingerprint /fake/docker/ctr/minerbot-bot /fake/docker/tags/minerbot-bot@current /fake/docker/tags/minerbot-bot@previous)
    in_home both 120 env FAKE_UNREADY="dashboard@${c5:0:12} dashboard@${c1:0:12}" ./deploy.sh
    expect_exit 1 "the dashboard ready on neither image"
    log=$(last_log deploy)
    expect_said "and it isn't ready on the image before it, ${c1:0:12}, either" "the dashboard ready on neither image" "$log"
    grep -qx 'RESULT failed' "$log" 2> /dev/null || problem "the dashboard ready on neither image: no RESULT failed"
    grep -qP "\tdeploy\tdashboard\t${c5:0:12}\tnot ready\tnor on ${c1:0:12}" "$home/logs/history.log" || problem "no 'nor on' history line"
    same_state "the bot was touched" "$bot_before" \
        "$(fingerprint /fake/docker/ctr/minerbot-bot /fake/docker/tags/minerbot-bot@current /fake/docker/tags/minerbot-bot@previous)"
    [ "$(tag_of minerbot-dashboard:current)" = "$(image_of "$c1" dashboard)" ] || problem "the dashboard's :current isn't c1's image again"

    restore deployed
    set_main "$c5"
    in_home restarted 120 env FAKE_RESTARTS="dashboard@${c5:0:12}" ./deploy.sh --only dashboard
    expect_exit 1 "the dashboard that restarts during the wait"
    expect_said "The dashboard isn't ready: its container restarted (restart count 1)." "the dashboard that restarts during the wait" \
        "$(last_log deploy)"
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$c1" dashboard)" ] || problem "the dashboard that restarted isn't back on c1"

    restore deployed
    set_main "$c5"
    in_home unhealthy 120 env FAKE_UNHEALTHY="dashboard@${c5:0:12}" ./deploy.sh --only dashboard
    expect_exit 1 "the dashboard Docker calls unhealthy"
    expect_said "The dashboard isn't ready: Docker's health check says unhealthy." "the dashboard Docker calls unhealthy" "$(last_log deploy)"

    restore deployed
    set_main "$c5"
    in_home flapping 120 env FAKE_BOT_RESTARTS_AT=3 ./deploy.sh --only bot
    expect_exit 1 "the bot that restarts right after its ready line"
    expect_said "The bot didn't stay up" "the bot that restarts right after its ready line" "$(last_log deploy)"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c1" bot)" ] || problem "the bot that didn't stay up isn't back on c1"
    verdict
}

case_33() {
    begin 33 "--only bot on a commit with a new migration builds the dashboard's image from it too, which carries the migration, dumps and migrates with it, and swaps only the bot"
    local dash_before
    restore deployed
    set_main "$c6"
    dash_before=$(ctr_image minerbot-dashboard)
    in_home only-bot 120 ./deploy.sh --only bot
    expect_exit 0 "--only bot with a migration"
    run_calls | args_of | grep -qF -- "--label org.opencontainers.image.revision=$c6 -f $home/src/MinerBot.Dashboard/Dockerfile -t minerbot-dashboard:new " \
        || problem "the dashboard's image, which carries the migrations, wasn't built from c6"
    in_order "the dump, then the migration" '^mariadb-dump ' ' run .* minerbot-dashboard:new migrate$'
    [ "$(cat /fake/db/version 2> /dev/null)" = 2 ] || problem "the database wasn't migrated to version 2"
    [ "$(ctr_image minerbot-dashboard)" = "$dash_before" ] || problem "--only bot swapped the dashboard"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c6" bot)" ] || problem "the bot wasn't swapped to c6"
    verdict
}

case_34() {
    begin 34 "time for the dashboard's swap but not the bot's: the dashboard is swapped, the bot isn't, and the run says so"
    restore deployed
    set_main "$c5"
    direct late-bot 120 MINERBOT_DEADLINE="$(($(date +%s) + 690))" -- ./deploy.sh
    expect_exit 1 "the run without time for the bot"
    expect_said "There isn't enough time left in this run for a safe swap of the bot. The dashboard was swapped, the bot wasn't." \
        "the run without time for the bot"
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$c5" dashboard)" ] || problem "the dashboard wasn't swapped"
    [ "$(ctr_image minerbot-bot)" = "$(image_of "$c1" bot)" ] || problem "the bot was swapped without the time for it"
    verdict
}

case_35() {
    begin 35 "a rollback onto an image that isn't ready either: exit 1, RESULT failed, a failed history line, and it's left on that image"
    local log
    restore deployed
    in_home back-unready 60 env FAKE_UNREADY="bot@" ./rollback.sh bot
    expect_exit 1 "the rollback that isn't ready"
    log=$(last_log rollback)
    expect_said "The bot isn't ready on image 000000000000 either (above)" "the rollback that isn't ready" "$log"
    grep -qx 'RESULT failed' "$log" 2> /dev/null || problem "the run log has no RESULT failed"
    grep -qP "\trollback\tbot\timage 000000000000\tfailed\tnot ready on it either" "$home/logs/history.log" || problem "no failed history line"
    [ "$(ctr_image minerbot-bot)" = "$old_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$old_bot" ] || problem "it isn't left on the image it went back to"
    verdict
}

case_36() {
    begin 36 "a service whose container is gone but whose :current is there swaps from :current, which becomes :previous"
    restore deployed
    set_main "$c5"
    rm -f /fake/docker/ctr/minerbot-bot
    in_home no-container 120 ./deploy.sh --only bot
    expect_exit 0 "the deploy without the bot's container"
    expect_said "Swapping the bot from ${c1:0:12} to ${c5:0:12}." "the deploy without the bot's container" "$(last_log deploy)"
    [ "$(tag_of minerbot-bot:previous)" = "$(image_of "$c1" bot)" ] || problem ":previous isn't the image :current named before"
    verdict
}

case_37() {
    begin 37 "a commit without a new migration: no dump and no migration; a deploy that stops writes its failed history line; a unit waits for a lock that's let go within its 10 seconds"
    restore deployed
    set_main "$c5"
    in_home no-migration 120 ./deploy.sh --only dashboard
    expect_exit 0 "the deploy without a migration"
    expect_said "No migrations are due." "the deploy without a migration" "$(last_log deploy)"
    ! run_calls | args_of | grep -qE '^mariadb-dump | run .* migrate$' || problem "it dumped or migrated without a new migration"

    restore deployed
    set_main "$c5"
    in_home settings-refused 120 env FAKE_CHECK_SETTINGS=fail ./deploy.sh --only dashboard
    expect_exit 1 "the deploy whose settings check fails"
    tail -n 1 "$home/logs/history.log" | grep -qP "\tdeploy\tdashboard\t${c5:0:12}\tfailed\tstopped while checking the dashboard's settings\t" \
        || problem "history.log's last line isn't the run's failed line" "$(tail -n 2 "$home/logs/history.log")"

    # A lock let go after 2 seconds: the unit's take_lock waits for it
    restore deployed
    set_main "$c5"
    hold_lock "$home/state/lock"
    (sleep 2; kill "$holder" 2> /dev/null) &
    direct waits 60 -- ./deploy.sh --only bot --no-start
    wait
    expect_exit 0 "the unit that waits for the lock"
    expect_said "RESULT ok" "the unit that waits for the lock"
    verdict
}

case_38() {
    begin 38 "Docker failing to answer about an image or a container stops the swap with its own message, and nothing is swapped; for the dashboard, the deploy stops before its rights, which need the commits of the dashboard images there"
    local before
    restore deployed
    set_main "$c5"
    before=$(docker_state | grep -v '@new$')
    in_home image-down 120 env FAKE_DOCKER_DOWN=image-inspect ./deploy.sh --only bot
    expect_exit 1 "the bot's deploy where Docker doesn't answer about images"
    expect_said "Docker didn't answer about the image minerbot-bot:new (above)." "the bot's deploy where Docker doesn't answer about images"
    same_state "a container or a tag changed" "$before" "$(docker_state | grep -v '@new$')"
    restore deployed
    set_main "$c5"
    before=$(docker_state | grep -v '@new$')
    in_home image-down-dashboard 120 env FAKE_DOCKER_DOWN=image-inspect ./deploy.sh --only dashboard
    expect_exit 1 "the dashboard's deploy where Docker doesn't answer about images"
    expect_said "Docker didn't answer about the image minerbot-dashboard:current (above). Nothing was swapped." \
        "the dashboard's deploy where Docker doesn't answer about images"
    ! run_calls | grep -qF '{stdin: apply}' || problem "the rights were applied though Docker didn't answer about the dashboard's images"
    same_state "a container or a tag changed" "$before" "$(docker_state | grep -v '@new$')"
    restore deployed
    set_main "$c5"
    before=$(docker_state | grep -v '@new$')
    in_home container-down 120 env FAKE_DOCKER_DOWN=container-inspect ./deploy.sh --only dashboard
    expect_exit 1 "the deploy where Docker doesn't answer about containers"
    expect_said "Docker didn't answer about the container minerbot-dashboard (above)." "the deploy where Docker doesn't answer about containers"
    ! run_calls | grep -qF '{stdin: apply}' || problem "the rights were applied though Docker didn't answer about the dashboard's container"
    same_state "a container or a tag changed" "$before" "$(docker_state | grep -v '@new$')"
    verdict
}

case_39() {
    begin 39 "die: a script's before_stop runs once, first, even when it stops itself; a detached run's die writes its failed history line and RESULT failed"
    local said_file=/tmp/mbtest-die.out
    rm -rf /tmp/mbtest-die-home
    # shellcheck disable=SC2016 # the commands are for the bash below
    bash -c 'source /repo/deploy/common.sh; home=/tmp/mbtest-die-home
        before_stop() { echo "before_stop ran"; die "a second stop, inside before_stop"; }
        die "the first stop"' > "$said_file" 2>&1
    status=$?
    expect_exit 1 "a die with before_stop"
    [ "$(grep -c 'before_stop ran' "$said_file")" = 1 ] && [ "$(head -n 1 "$said_file")" = "the first stop" ] \
        || problem "before_stop didn't run once, right after the first message" "$(cat "$said_file")"
    # shellcheck disable=SC2016
    bash -c 'source /repo/deploy/common.sh; home=/tmp/mbtest-die-home; run_mode=detached; action=deploy; subject=bot
        rev=0123456789ab; step="doing a made-up step"; die "a made-up stop"' > "$said_file" 2>&1
    status=$?
    expect_exit 1 "a detached die"
    grep -qx 'RESULT failed' "$said_file" || problem "a detached die printed no RESULT failed" "$(cat "$said_file")"
    tail -n 1 /tmp/mbtest-die-home/logs/history.log 2> /dev/null | grep -qP "\tdeploy\tbot\t0123456789ab\tfailed\tstopped while doing a made-up step$" \
        || problem "a detached die wrote no failed history line" "$(cat /tmp/mbtest-die-home/logs/history.log 2>&1)"
    rm -rf /tmp/mbtest-die-home
    verdict
}

# shellcheck disable=SC2016 # the backticks are the rights file's own text, never run
case_60() {
    begin 60 "a dashboard older than the installed deploy files keeps its rights: it gets the rights the last apply used and each GRANT of its own dashboard-rights.sql they lack, named in the log, and that is the copy setup applies; run again, nothing more is added; the next deploy of the newer dashboard applies the installed file and the GRANTs of the older dashboard's own file, since it replaces that one, and drops a right only the last apply had; before any apply kept a copy, the installed file is the base"
    local dropped line copy=$home/state/dashboard-rights.applied.sql applied=/fake/db/applied.sql
    local who="'minerbot-dashboard'@'localhost', 'minerbot-dashboard'@'127.0.0.1'" grant marker
    rendered() { sed -e 's/{{SITE_DB}}/website_db/g' -e 's/{{BOT_DB}}/minerbot_db/g' -e "s/{{GRANTEES}}/$who/g"; }
    grant="GRANT INSERT ON \`minerbot_db\`.\`audit_log\` TO $who;"
    marker="GRANT SELECT ON \`minerbot_db\`.\`mbtest_marker\` TO $who;"
    restore deployed
    # A later pull request removes INSERT on audit_log, which c1's dashboard uses: the rule allows that once the code
    # stopped using it, a pull request before
    dropped=$(new_commit "$c1" rights-dropped sed -i '/^GRANT INSERT ON `{{BOT_DB}}`\.`audit_log` TO {{GRANTEES}};$/d' \
        deploy/dashboard-rights.sql) || problem "couldn't make the commit that removes a right"
    ! G -C /work/origin.git show "$dropped:deploy/dashboard-rights.sql" | grep -qF '`audit_log`' \
        || problem "the test's commit didn't remove INSERT on audit_log"
    set_main "$dropped"
    in_home newer 120 ./deploy.sh --only dashboard
    expect_exit 0 "the deploy of the newer dashboard"
    # c1's dashboard, which it replaced, keeps INSERT on audit_log while rollback.sh can put it back (case 64). Long
    # after, no image of c1's dashboard is left, and the newer dashboard deployed again applies its file alone.
    cp /fake/docker/tags/minerbot-dashboard@current /fake/docker/tags/minerbot-dashboard@previous
    in_home newer-alone 120 ./deploy.sh --only dashboard
    expect_exit 0 "the newer dashboard again, with no image of c1's dashboard left"
    ! grep -qxF "$grant" "$applied" 2> /dev/null || problem "the newer dashboard's deploy gave INSERT on audit_log"
    cmp -s "$home/dashboard-rights.sql" "$copy" || problem "after the newer dashboard's deploy, the copy setup applies isn't the installed file"
    # The last apply used a right more than the installed file gives, which the dashboard that runs may use, as after a
    # --only bot deploy that installed a newer file
    echo 'GRANT SELECT ON `{{BOT_DB}}`.`mbtest_marker` TO {{GRANTEES}};' >> "$copy"

    # The messages are checked in what the terminal followed: two deploys begun in the same second have logs that
    # last_log can't tell apart
    in_home older 120 ./deploy.sh "$c1" --only dashboard
    expect_exit 0 "the deploy of c1's older dashboard"
    expect_said "Kept the deploy files of ${dropped:0:12}, which are newer than ${c1:0:12}." "the older dashboard"
    expect_said "so the dashboard of ${c1:0:12} gets the rights the last apply used, from state/dashboard-rights.applied.sql, and these GRANT statements of its own dashboard-rights.sql" \
        "the older dashboard"
    expect_said '  GRANT INSERT ON `{{BOT_DB}}`.`audit_log` TO {{GRANTEES}};' "the older dashboard"
    grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "c1's dashboard didn't get INSERT on audit_log, which its code uses" "$(grep -F GRANT "$applied" 2>&1)"
    grep -qxF "$marker" "$applied" 2> /dev/null || problem "a right the last apply used was taken from the dashboard that ran"
    # Every right the newer file gives stays, for the newer dashboard, which is :previous now
    while IFS= read -r line; do
        grep -qxF -- "$line" "$applied" 2> /dev/null || problem "a right the newer file gives is missing: $line"
    done < <(grep '^GRANT ' "$home/dashboard-rights.sql" | rendered)
    in_order "the rights, then the swap" '\{stdin: apply\}' ' up -d --no-build --no-deps dashboard$'
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$c1" dashboard)" ] || problem "the dashboard doesn't run c1's image"
    [ "$(tag_of minerbot-dashboard:previous)" = "$(image_of "$dropped" dashboard)" ] || problem ":previous isn't the newer dashboard"
    grep -qF '`audit_log`' "$copy" 2> /dev/null && [ "$(stat -c %a "$copy")" = 600 ] \
        || problem "the copy setup applies lacks INSERT on audit_log, or isn't 600"
    [ -z "$(find "$home/state/tmp" -mindepth 1 -maxdepth 1 -name 'deploy.*' 2> /dev/null)" ] \
        || problem "the deploy left its rights file in state/tmp"

    # The same commit again: the copy has every right its file gives, so nothing more is added
    in_home older-again 120 ./deploy.sh "$c1" --only dashboard
    expect_exit 0 "c1's dashboard again"
    expect_said "Its own dashboard-rights.sql gives no right they lack." "c1's dashboard again"
    grep -qxF "$grant" "$applied" 2> /dev/null || problem "c1's dashboard lost INSERT on audit_log when it was deployed again"

    # The newer dashboard again: the installed file, and the GRANTs of c1's own file, since c1's dashboard, which it
    # replaces, becomes :previous; the right only the last apply had goes
    in_home newer-again 120 ./deploy.sh --only dashboard
    expect_exit 0 "the newer dashboard again"
    ! grep -qF -e "$marker" "$applied" 2> /dev/null || problem "the newer dashboard's deploy kept a right no dashboard's own file gives"
    grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "the newer dashboard's deploy took INSERT on audit_log from c1's dashboard, which it replaced"
    grep -qF '`audit_log`' "$copy" 2> /dev/null && ! grep -qF mbtest_marker "$copy" 2> /dev/null \
        || problem "the copy setup applies isn't the installed file and c1's GRANT"

    # Before any apply kept a copy: a --only bot deploy installs the newer files, and c1's dashboard gets the installed
    # file and the right it lacks. The restore rebuilds GitHub's stand-in without the commit above, so it's made again.
    restore deployed
    dropped=$(new_commit "$c1" rights-dropped sed -i '/^GRANT INSERT ON `{{BOT_DB}}`\.`audit_log` TO {{GRANTEES}};$/d' \
        deploy/dashboard-rights.sql) || problem "couldn't make the commit that removes a right again"
    set_main "$dropped" || problem "couldn't move main to the commit that removes a right"
    in_home bot-only 120 ./deploy.sh --only bot
    expect_exit 0 "the bot's deploy that installs the newer files"
    [ ! -e "$copy" ] || problem "the test's premise: no apply has kept a copy yet"
    in_home older-no-copy 120 ./deploy.sh "$c1" --only dashboard
    expect_exit 0 "c1's dashboard before any copy"
    expect_said "so the dashboard of ${c1:0:12} gets the installed dashboard-rights.sql, since no apply has kept a copy yet, and these GRANT statements" \
        "c1's dashboard before any copy"
    grep -qxF "$grant" "$applied" 2> /dev/null || problem "c1's dashboard didn't get INSERT on audit_log before any copy"
    verdict
}

case_61() {
    begin 61 "a swap cut off after it moved the tags: rollback.sh run again finishes it on :current, whether Docker had stopped the old container or it still runs, removes the replacement Compose left half made, moves no tag, and the run after goes back as usual; a deploy's swap cut off the same way is finished too"
    local c1_bot c1_dash c5_dash cid
    c1_bot=$(image_of "$c1" bot)
    c1_dash=$(image_of "$c1" dashboard)
    c5_dash=$(image_of "$c5" dashboard)
    restore deployed
    # The messages are checked in what the terminal followed: two rollbacks begun in the same second have logs that
    # last_log can't tell apart
    in_home cut 60 env MINERBOT_TEST_CUT_AFTER=tags ./rollback.sh bot
    expect_exit 1 "the rollback cut off after it moved the tags"
    expect_said "Cut off after step tags (MINERBOT_TEST_CUT_AFTER)." "the rollback cut off after it moved the tags"
    [ "$(tag_of minerbot-bot:current)" = "$old_bot" ] && [ "$(tag_of minerbot-bot:previous)" = "$c1_bot" ] \
        && [ "$(ctr_image minerbot-bot)" = "$c1_bot" ] || problem "the cut run didn't leave the tags moved and the bot on c1's image"
    # Docker had stopped the old container through its API, so a restart doesn't start it again, and Compose had made
    # its replacement under a temporary name, the old container's ID and its name, and never started it
    cid=$(cut -d' ' -f7 /fake/docker/ctr/minerbot-bot)
    awk '{ $2 = "false"; $6 = 0; print }' /fake/docker/ctr/minerbot-bot > /tmp/mbtest-ctr && mv /tmp/mbtest-ctr /fake/docker/ctr/minerbot-bot
    echo "$old_bot false 0 0001-01-01T00:00:00Z none 0 $(printf 'half made' | sha256sum | cut -c1-64) cfg-0" \
        > "/fake/docker/ctr/${cid:0:12}_minerbot-bot"
    in_home again 60 ./rollback.sh bot
    expect_exit 0 "rollback.sh run again after the cut"
    expect_said "The bot's container has ${c1:0:12}, but minerbot-bot:current is image 000000000000: a deploy or rollback was cut off after it moved the tags" \
        "rollback.sh run again after the cut"
    expect_said "The bot runs image 000000000000 and is ready: the swap that was cut off is finished. Run ./rollback.sh bot again to go back to ${c1:0:12}." \
        "rollback.sh run again after the cut"
    [ "$(ctr_image minerbot-bot)" = "$old_bot" ] && [ "$(cut -d' ' -f2 /fake/docker/ctr/minerbot-bot)" = true ] \
        || problem "the bot doesn't run the image its cut rollback went to" "$(cat /fake/docker/ctr/minerbot-bot 2>&1)"
    [ ! -e "/fake/docker/ctr/${cid:0:12}_minerbot-bot" ] || problem "the replacement Compose left half made is still there"
    [ "$(tag_of minerbot-bot:current)" = "$old_bot" ] && [ "$(tag_of minerbot-bot:previous)" = "$c1_bot" ] || problem "a tag moved"
    grep -qP "\trollback\tbot\timage 000000000000\tok\tfinished a cut-off swap, was ${c1:0:12}\t" "$home/logs/history.log" \
        || problem "no history line for the finished swap" "$(tail -n 3 "$home/logs/history.log")"
    in_home forward 60 ./rollback.sh bot
    expect_exit 0 "the run after"
    [ "$(ctr_image minerbot-bot)" = "$c1_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$c1_bot" ] \
        && [ "$(tag_of minerbot-bot:previous)" = "$old_bot" ] || problem "the run after didn't go back to c1's image as usual" "$(excerpt)"

    # Cut off before Docker stopped anything, and the old container still runs, as after a restart that started it again
    restore deployed
    in_home cut-running 60 env MINERBOT_TEST_CUT_AFTER=tags ./rollback.sh dashboard
    expect_exit 1 "the dashboard's rollback cut off after it moved the tags"
    in_home again-running 60 ./rollback.sh dashboard
    expect_exit 0 "rollback.sh run again while the old dashboard runs"
    [ "$(ctr_image minerbot-dashboard)" = "$old_dashboard" ] && [ "$(tag_of minerbot-dashboard:current)" = "$old_dashboard" ] \
        && [ "$(tag_of minerbot-dashboard:previous)" = "$c1_dash" ] \
        || problem "the dashboard isn't on the image its cut rollback went to, with the tags as the cut left them" "$(excerpt)"

    # A deploy's swap cut off the same way: rollback.sh finishes it on the deploy's image, then goes back as usual
    restore deployed
    set_main "$c5"
    in_home deploy-cut 120 env MINERBOT_TEST_CUT_AFTER=tags-dashboard ./deploy.sh --only dashboard
    expect_exit 1 "the deploy cut off after it moved the dashboard's tags"
    [ "$(tag_of minerbot-dashboard:current)" = "$c5_dash" ] && [ "$(ctr_image minerbot-dashboard)" = "$c1_dash" ] \
        || problem "the cut deploy didn't leave the dashboard's tags moved and the dashboard on c1's image"
    in_home deploy-finished 60 ./rollback.sh dashboard
    expect_exit 0 "rollback.sh after the deploy's cut"
    expect_said "the swap that was cut off is finished. Run ./rollback.sh dashboard again to go back to ${c1:0:12}." \
        "rollback.sh after the deploy's cut"
    [ "$(ctr_image minerbot-dashboard)" = "$c5_dash" ] || problem "the deploy's cut swap wasn't finished on c5's image"
    in_home deploy-back 60 ./rollback.sh dashboard
    expect_exit 0 "the rollback after that"
    [ "$(ctr_image minerbot-dashboard)" = "$c1_dash" ] || problem "the rollback after that didn't go back to c1's image"
    verdict
}

case_62() {
    begin 62 "the terminal follows a run to its end while systemctl answers nothing, as one that hit its 10 seconds under load would: two such answers in a row while the unit runs don't end the wait, and the run's own result is the exit code"
    restore deployed
    # The stand-in systemctl, but its 2nd and 3rd is-active of a unit that runs answer nothing: no line, exit code 1.
    # The case puts the stand-in back when it ends, however it ends.
    trap 'if [ -e /usr/local/bin/systemctl.mbtest-62 ]; then mv -f /usr/local/bin/systemctl.mbtest-62 /usr/local/bin/systemctl; fi' EXIT
    if mv /usr/local/bin/systemctl /usr/local/bin/systemctl.mbtest-62; then
        cat > /usr/local/bin/systemctl << 'EOF'
#!/usr/bin/env bash
if [ "${1:-}" = is-active ]; then
    pid=''
    { read -r pid < "/fake/units/$2.pid"; } 2> /dev/null || pid=''
    if [ -n "$pid" ] && kill -0 "$pid" 2> /dev/null; then
        n=$(($(cat /fake/units/mbtest-alive 2> /dev/null || echo 0) + 1))
        echo "$n" > /fake/units/mbtest-alive
        if [ "$n" = 2 ] || [ "$n" = 3 ]; then
            echo "$n" >> /fake/units/mbtest-silent
            exit 1
        fi
    fi
fi
exec /usr/local/bin/systemctl.mbtest-62 "$@"
EOF
        chmod 755 /usr/local/bin/systemctl
    else
        problem "the test couldn't set the stand-in systemctl aside"
    fi
    in_home silent 60 ./rollback.sh bot
    mv -f /usr/local/bin/systemctl.mbtest-62 /usr/local/bin/systemctl
    [ "$(grep -c . /fake/units/mbtest-silent 2> /dev/null)" = 2 ] \
        || problem "the test's premise: systemctl didn't answer nothing twice while the rollback's unit ran" "$(cat /fake/units/mbtest-alive 2>&1)"
    expect_exit 0 "the rollback the terminal followed through two silent answers"
    ! said "It still runs after" && ! said "It ended without a result" \
        || problem "the terminal stopped following while the run went on" "$(excerpt)"
    expect_said "RESULT ok" "the rollback the terminal followed through two silent answers"
    [ "$(ctr_image minerbot-bot)" = "$old_bot" ] || problem "the rollback didn't go back to the bot's image before"
    verdict
}

# Case 63's stand-in docker: Compose's second up of the bot, which is a failed swap's way back to the image before,
# fails as FAKE_UP_FAIL makes it fail; with MBTEST_GONE=1, Compose removed the bot's container first; with
# MBTEST_KEEP=1, it exits 0 instead, but leaves the container as it was, as FAKE_UP_KEEP makes it
wrap_docker_63() {
    mv /usr/local/bin/docker /usr/local/bin/docker.mbtest-63 || return 1
    cat > /usr/local/bin/docker << 'EOF'
#!/usr/bin/env bash
if [ "${1:-} ${2:-}" = "compose -f" ] && [[ " $* " == *" up -d --no-build --no-deps bot "* ]]; then
    n=$(($(cat /fake/docker/mbtest-bot-ups 2> /dev/null || echo 0) + 1))
    echo "$n" > /fake/docker/mbtest-bot-ups
    if [ "$n" = 2 ]; then
        if [ "${MBTEST_KEEP:-}" = 1 ]; then
            FAKE_UP_KEEP=bot exec /usr/local/bin/docker.mbtest-63 "$@"
        fi
        if [ "${MBTEST_GONE:-}" = 1 ]; then
            rm -f /fake/docker/ctr/minerbot-bot
        fi
        FAKE_UP_FAIL=bot exec /usr/local/bin/docker.mbtest-63 "$@"
    fi
fi
exec /usr/local/bin/docker.mbtest-63 "$@"
EOF
    chmod 755 /usr/local/bin/docker
}

unwrap_docker_63() {
    if [ -e /usr/local/bin/docker.mbtest-63 ]; then mv -f /usr/local/bin/docker.mbtest-63 /usr/local/bin/docker; fi
}

case_63() {
    begin 63 "a swap that fails where Compose doesn't put the image before back, whether it fails or exits 0 without recreating the container: the deploy says what runs, the new image or, once the container is gone, nothing, and never that the service is back or that the problem is outside the image; the history line says so; and rollback.sh then finishes the swap back"
    local log c1_bot c5_bot
    c1_bot=$(image_of "$c1" bot)
    c5_bot=$(image_of "$c5" bot)
    trap unwrap_docker_63 EXIT
    restore deployed
    set_main "$c5"
    wrap_docker_63 || problem "the test couldn't set the stand-in docker aside"
    in_home back-fails 120 env FAKE_UNREADY="bot@${c5:0:12}" ./deploy.sh --only bot
    unwrap_docker_63
    expect_exit 1 "the deploy whose way back Compose doesn't take"
    log=$(last_log deploy)
    expect_said "The bot on ${c5:0:12} wasn't ready, and Compose didn't put it back on ${c1:0:12} (above), so it's left on ${c5:0:12}, while minerbot-bot:current names ${c1:0:12}. Run ./rollback.sh bot once Docker answers: it finishes the swap back." \
        "the deploy whose way back Compose doesn't take" "$log"
    ! said "outside the image" "$log" && ! said "It's left on ${c1:0:12}" "$log" \
        || problem "the deploy said the bot is left on the image before, or blamed something outside the image" "$(excerpt "$log")"
    [ "$(ctr_image minerbot-bot)" = "$c5_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$c1_bot" ] \
        && [ "$(tag_of minerbot-bot:previous)" = "$old_bot" ] && [ "$(tag_of minerbot-bot:failed)" = "$c5_bot" ] \
        || problem "the bot's container and tags aren't what the deploy said: c5's image runs, and :current is c1's"
    grep -qP "\tdeploy\tbot\t${c5:0:12}\tnot ready\tnot put back on ${c1:0:12}, left on ${c5:0:12}\t" "$home/logs/history.log" \
        || problem "no history line that says the bot wasn't put back" "$(tail -n 3 "$home/logs/history.log")"
    # What the deploy says to do: rollback.sh puts the bot back on :current, and moves no tag
    in_home finish 60 ./rollback.sh bot
    expect_exit 0 "rollback.sh after the deploy whose way back Compose didn't take"
    expect_said "or Compose didn't put a failed swap back. This run finishes that swap on ${c1:0:12}, and moves no tag." \
        "rollback.sh after the deploy whose way back Compose didn't take"
    [ "$(ctr_image minerbot-bot)" = "$c1_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$c1_bot" ] \
        && [ "$(tag_of minerbot-bot:previous)" = "$old_bot" ] \
        || problem "rollback.sh didn't finish the swap back onto c1's image, with the tags as they were" "$(excerpt)"

    # Compose removed the bot's container before it failed: the deploy says it doesn't run, and how to start it
    restore deployed
    set_main "$c5"
    wrap_docker_63 || problem "the test couldn't set the stand-in docker aside"
    in_home gone 120 env FAKE_UNREADY="bot@${c5:0:12}" MBTEST_GONE=1 ./deploy.sh --only bot
    unwrap_docker_63
    expect_exit 1 "the deploy whose way back left no container"
    log=$(last_log deploy)
    expect_said "The bot on ${c5:0:12} wasn't ready, and Compose didn't put it back on ${c1:0:12} (above), and its container is gone, so it doesn't run. Start it on ${c1:0:12}, which minerbot-bot:current names, with: docker compose up -d --no-build --no-deps bot." \
        "the deploy whose way back left no container" "$log"
    grep -qP "\tdeploy\tbot\t${c5:0:12}\tnot ready\tnot put back on ${c1:0:12}, not running\t" "$home/logs/history.log" \
        || problem "no history line that says the bot doesn't run" "$(tail -n 3 "$home/logs/history.log")"

    # Compose exits 0 on the way back but leaves the container on the new image: the deploy goes by the image the
    # container runs, not by Compose's exit code, so it says the same as when Compose fails
    restore deployed
    set_main "$c5"
    wrap_docker_63 || problem "the test couldn't set the stand-in docker aside"
    in_home back-kept 120 env FAKE_UNREADY="bot@${c5:0:12}" MBTEST_KEEP=1 ./deploy.sh --only bot
    unwrap_docker_63
    expect_exit 1 "the deploy whose way back Compose exits 0 on without recreating the bot"
    log=$(last_log deploy)
    expect_said "The bot on ${c5:0:12} wasn't ready, and Compose didn't put it back on ${c1:0:12} (above), so it's left on ${c5:0:12}, while minerbot-bot:current names ${c1:0:12}." \
        "the deploy whose way back Compose exits 0 on without recreating the bot" "$log"
    ! said "outside the image" "$log" \
        || problem "the deploy whose way back Compose exits 0 on blamed something outside the image" "$(excerpt "$log")"
    [ "$(ctr_image minerbot-bot)" = "$c5_bot" ] && [ "$(tag_of minerbot-bot:current)" = "$c1_bot" ] \
        || problem "the bot's container and tags aren't what the deploy said, after a way back Compose exited 0 on"
    verdict
}

# shellcheck disable=SC2016 # the backticks are the rights file's own text, never run
case_64() {
    begin 64 "after going back two pull requests, every dashboard the swap or rollback.sh can put back keeps the GRANTs of its own dashboard-rights.sql: the one a forward deploy replaces, so a swap that fails goes back to it with its rights, and :previous, after a swap that works and when the same commit is deployed again; a right goes once no such dashboard has it in its own file; and on their own, :current after a deploy cut off once it moved the tags, and the image the container runs when a failed swap left it on one that neither tag names"
    local dropped later c1_dash b c x y applied=/fake/db/applied.sql copy=$home/state/dashboard-rights.applied.sql
    local who="'minerbot-dashboard'@'localhost', 'minerbot-dashboard'@'127.0.0.1'" grant
    grant="GRANT INSERT ON \`minerbot_db\`.\`audit_log\` TO $who;"
    c1_dash=$(image_of "$c1" dashboard)
    restore deployed
    # A later pull request removes INSERT on audit_log, which c1's dashboard uses, as after a pull request in between
    # whose code stopped using it
    dropped=$(new_commit "$c1" rights-dropped-64 sed -i '/^GRANT INSERT ON `{{BOT_DB}}`\.`audit_log` TO {{GRANTEES}};$/d' \
        deploy/dashboard-rights.sql) || problem "couldn't make the commit that removes a right"
    set_main "$dropped"
    # The messages are checked in what the terminal followed, as in case 60
    in_home newer 120 ./deploy.sh --only dashboard
    expect_exit 0 "the deploy of the newer dashboard"
    expect_said "The dashboard of ${c1:0:12}, which a failed swap or ./rollback.sh dashboard can put back, keeps these GRANT statements of its own dashboard-rights.sql, which the installed dashboard-rights.sql lacks:" \
        "the deploy of the newer dashboard"
    grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "the newer dashboard's deploy took INSERT on audit_log from c1's dashboard, which it replaced" "$(grep -F GRANT "$applied" 2>&1)"
    in_home older 120 ./deploy.sh "$c1" --only dashboard
    expect_exit 0 "the deploy of c1's older dashboard"

    # Forward again, and the newer dashboard isn't ready: the swap goes back to c1's, which keeps its rights
    in_home forward-unready 120 env FAKE_UNREADY="dashboard@${dropped:0:12}" ./deploy.sh --only dashboard
    expect_exit 1 "the forward deploy whose dashboard isn't ready"
    expect_said "so it's back on ${c1:0:12}, which is ready." "the forward deploy whose dashboard isn't ready"
    [ "$(ctr_image minerbot-dashboard)" = "$c1_dash" ] || problem "the dashboard isn't back on c1's image"
    grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "the swap went back to c1's dashboard, but the rights it left lack INSERT on audit_log, which c1's code uses"

    # Forward again, ready: c1's dashboard is :previous and keeps its rights, also when the same commit is deployed
    # again, as after a deploy whose bot didn't swap; rollback.sh, which never touches the rights, puts it back under
    # them
    in_home forward 120 ./deploy.sh --only dashboard
    expect_exit 0 "the forward deploy that is ready"
    [ "$(tag_of minerbot-dashboard:previous)" = "$c1_dash" ] || problem ":previous isn't c1's dashboard"
    grep -qxF "$grant" "$applied" 2> /dev/null || problem "the forward deploy took INSERT on audit_log from c1's dashboard, now :previous"
    in_home same-again 120 ./deploy.sh --only dashboard
    expect_exit 0 "the same commit again"
    expect_said "The dashboard is already on ${dropped:0:12}." "the same commit again"
    grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "the same commit deployed again took INSERT on audit_log from c1's dashboard, still :previous"
    in_home back 60 ./rollback.sh dashboard
    expect_exit 0 "rollback.sh back to c1's dashboard"
    [ "$(ctr_image minerbot-dashboard)" = "$c1_dash" ] && grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "rollback.sh didn't put c1's dashboard back under rights with INSERT on audit_log"
    in_home forward-back 60 ./rollback.sh dashboard
    expect_exit 0 "rollback.sh forward again"

    # A later commit swapped in makes the newer dashboard :previous; deployed again, no dashboard here has c1's right in
    # its own file any more, and the right goes
    later=$(new_commit "$dropped" later-64 sh -c 'echo later > version.txt') || problem "couldn't make a later commit"
    set_main "$later"
    in_home later 120 ./deploy.sh --only dashboard
    expect_exit 0 "the deploy of a later commit"
    grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "the later commit's deploy took INSERT on audit_log while c1's dashboard was still :previous"
    in_home later-again 120 ./deploy.sh --only dashboard
    expect_exit 0 "the later commit again"
    ! grep -qxF "$grant" "$applied" 2> /dev/null \
        || problem "INSERT on audit_log stayed though no dashboard that runs, :current or :previous has it in its own file"
    cmp -s "$home/dashboard-rights.sql" "$copy" || problem "the copy setup applies isn't the installed file alone"

    # :current on its own: a deploy of B, which adds a right, is cut off once it moved the tags, so :current names B's
    # dashboard while c1's still runs and is :previous. The next deploy, of C, which takes B's right away, keeps it:
    # ./rollback.sh dashboard would finish the cut-off swap onto B.
    restore deployed
    b=$(new_commit "$c1" grant-b-64 sh -c 'echo "GRANT SELECT ON \`{{BOT_DB}}\`.\`mbtest_b\` TO {{GRANTEES}};" >> deploy/dashboard-rights.sql') \
        || problem "couldn't make the commit that adds a right"
    set_main "$b"
    in_home cut-at-tags 120 env MINERBOT_TEST_CUT_AFTER=tags-dashboard ./deploy.sh --only dashboard
    [ "$(ctr_image minerbot-dashboard)" = "$c1_dash" ] && [ "$(tag_of minerbot-dashboard:current)" = "$(image_of "$b" dashboard)" ] \
        && [ "$(tag_of minerbot-dashboard:previous)" = "$c1_dash" ] \
        || problem "after the deploy cut off once it moved the tags, c1's dashboard doesn't run with :current on B's and :previous on c1's" "$(excerpt)"
    c=$(new_commit "$b" ungrant-b-64 sed -i '/mbtest_b/d' deploy/dashboard-rights.sql) \
        || problem "couldn't make the commit that takes the right away"
    set_main "$c"
    in_home after-cut 120 ./deploy.sh --only dashboard
    expect_exit 0 "the deploy after the one cut off once it moved the tags"
    expect_said "The dashboard of ${b:0:12}, which a failed swap or ./rollback.sh dashboard can put back, keeps these GRANT statements of its own dashboard-rights.sql, which the installed dashboard-rights.sql lacks:" \
        "the deploy after the one cut off once it moved the tags"
    grep -qF '`mbtest_b`' "$applied" 2> /dev/null \
        || problem "the deploy after the cut-off one dropped the right of B's dashboard, which :current named"

    # The image the container runs on its own: X's dashboard runs while :current names c1's and :previous the old one,
    # as a failed swap that Compose didn't put back leaves it. The next deploy, of Y, which takes X's right away, keeps
    # it: its swap goes back to X when it fails, and X becomes :previous when it works.
    restore deployed
    x=$(new_commit "$c1" grant-x-64 sh -c 'echo "GRANT SELECT ON \`{{BOT_DB}}\`.\`mbtest_x\` TO {{GRANTEES}};" >> deploy/dashboard-rights.sql') \
        || problem "couldn't make the commit that adds a right"
    set_main "$x"
    in_home build-x 120 ./deploy.sh --only dashboard --no-start
    expect_exit 0 "the build of the commit that adds a right"
    awk -v id="$(image_of "$x" dashboard)" '{ $1 = id; print }' /fake/docker/ctr/minerbot-dashboard > /tmp/mbtest-ctr-64 \
        && mv /tmp/mbtest-ctr-64 /fake/docker/ctr/minerbot-dashboard
    [ "$(ctr_image minerbot-dashboard)" = "$(image_of "$x" dashboard)" ] && [ "$(tag_of minerbot-dashboard:current)" = "$c1_dash" ] \
        && [ "$(tag_of minerbot-dashboard:previous)" != "$(image_of "$x" dashboard)" ] \
        || problem "the test's start: X's dashboard doesn't run while :current names c1's and :previous another"
    y=$(new_commit "$x" ungrant-x-64 sed -i '/mbtest_x/d' deploy/dashboard-rights.sql) \
        || problem "couldn't make the commit that takes the right away"
    set_main "$y"
    in_home after-kept 120 ./deploy.sh --only dashboard
    expect_exit 0 "the deploy after a failed swap left another image running"
    grep -qF '`mbtest_x`' "$applied" 2> /dev/null \
        || problem "the deploy dropped the right of X's dashboard, which ran while its rights applied"
    [ "$(tag_of minerbot-dashboard:previous)" = "$(image_of "$x" dashboard)" ] \
        || problem "X's dashboard, which ran before the swap, isn't :previous after it"
    verdict
}

case_13() {
    begin 13 "the first setup through a real terminal: CRLF pastes with neither the certificate nor the key shown, the Access team tidied and its keys checked, the answers 600 in a 700 folder when the unit starts, the login made through stdin, the rights, dashboard.env 600 in the exact format, nginx's files and modes, the standalone test with the main context and then one reload, the 403 check, no dashboard started, state/tmp empty after; and both of Docker's readers take dashboard.env the same way"
    local cert key env_key value line name
    restore built
    cert=$(paste_of /work/certs/good-cert.pem)$'\n'
    key=$(paste_of /work/certs/good-key.pem)$'\n'
    converse setup-first "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "$cert" \
        "!Paste its private key" "$key" \
        "MinerBot Dashboard's client ID" "$client_id"$'\r' \
        "!Its client secret" "$secret1"$'\r' \
        "The owners' Discord user IDs" "100000000000000001, 100000000000000002"$'\r' \
        "The admins' Discord user IDs" "none"$'\r' \
        "The Cloudflare Access team domain" "https://MyTeam.cloudflareaccess.com/"$'\r' \
        "The Access application's AUD tag" "$(printf '%s' "$aud" | tr a-f A-F)"$'\r' \
        "Go ahead? Type yes" "yes"$'\r'
    expect_exit 0 "the first setup"
    ! said "BEGIN PRIVATE KEY" || problem "the key's BEGIN line is in the transcript"
    ! said "$(sed -n 2p /work/certs/good-key.pem)" || problem "the key's body is in the transcript"
    ! said "$secret1" || problem "the client secret is in the transcript"
    ! said "$(sed -n 2p /work/certs/good-cert.pem)" || problem "the certificate's paste is in the transcript"
    expect_said "(The certificate arrived.)" "the first setup"
    expect_said "The certificate names $host_name only." "the first setup"
    [ "$(cat /fake/units/minerbot-setup.answers 2> /dev/null)" = "700 600" ] \
        || problem "the answers' folder and file weren't 700 and 600 when the unit started" "$(cat /fake/units/minerbot-setup.answers 2> /dev/null)"
    run_calls | args_of | grep -qE '^mariadb \[timeout -k 10 30\] --batch --skip-print-query-on-error \{stdin: login\}$' \
        || problem "the login wasn't made through stdin with --skip-print-query-on-error"
    [ -f /fake/db/login-localhost ] && [ -f /fake/db/login-127.0.0.1 ] || problem "the login wasn't made on both hosts"
    run_calls | grep -qF '{stdin: rehearsal}' && run_calls | grep -qF '{stdin: apply}' || problem "the rights weren't rehearsed and applied"
    [ "$(stat -c %a "$home/dashboard.env" 2> /dev/null)" = 600 ] || problem "dashboard.env isn't 600"
    for name in DefaultConnection:website_db BotConnection:minerbot_db; do
        grep -qE "^ConnectionStrings__${name%%:*}=Server=127\.0\.0\.1;Port=3306;Database=${name#*:};Uid=minerbot-dashboard;Pwd=[A-Za-z0-9]{40};SslMode=Disabled;Connection Timeout=15;CharSet=utf8mb4;Maximum Pool Size=6;$" \
            "$home/dashboard.env" || problem "ConnectionStrings__${name%%:*} isn't in the exact format"
    done
    for line in "Dashboard__PublicOrigin=https://$host_name" "Discord__ClientId=$client_id" "Discord__ClientSecret=$secret1" \
        "Dashboard__Owners=$owners" "Dashboard__Admins=" "CloudflareAccess__TeamDomain=myteam.cloudflareaccess.com" \
        "CloudflareAccess__Audience=$aud"; do
        grep -qxF -- "$line" "$home/dashboard.env" || problem "dashboard.env hasn't the line ${line%%=*}=<as typed, tidied>"
    done
    env_key=$(sed -n 's/^Dashboard__ProxyKey=//p' "$home/dashboard.env")
    [[ $env_key =~ ^[0-9a-f]{64}$ ]] || problem "Dashboard__ProxyKey isn't 64 hex"
    [ "$(cat /etc/nginx/minerbot/proxy-key.conf 2> /dev/null)" = "proxy_set_header X-MinerBot-Proxy-Key \"$env_key\";" ] \
        || problem "nginx's proxy key isn't dashboard.env's"
    [ "$(cat /fake/db/password 2> /dev/null)" = "$(sed -n 's/.*;Pwd=\([^;]*\);.*/\1/p' "$home/dashboard.env" | head -n 1)" ] \
        || problem "the login's password isn't dashboard.env's"
    [ "$(stat -c %a /etc/nginx/minerbot 2> /dev/null)" = 700 ] || problem "/etc/nginx/minerbot isn't 700"
    for name in origin-cert.pem origin-key.pem proxy-key.conf; do
        [ "$(stat -c %a "/etc/nginx/minerbot/$name" 2> /dev/null)" = 600 ] || problem "/etc/nginx/minerbot/$name isn't 600"
    done
    [ "$(stat -c %a /etc/nginx/conf.d/minerbot-dashboard.conf 2> /dev/null)" = 644 ] || problem "the conf.d file isn't 644"
    cmp -s /etc/nginx/conf.d/minerbot-dashboard.conf "$home/minerbot-dashboard.conf" || problem "the conf.d file isn't the reference copy"
    cmp -s /etc/nginx/minerbot/origin-key.pem /work/certs/good-key.pem || problem "the key in nginx's folder isn't the pasted one"
    cmp -s /etc/nginx/minerbot/origin-cert.pem /work/certs/good-cert.pem || problem "the certificate in nginx's folder isn't the pasted one"
    in_order "nginx: the standalone test, the full test, one reload, the 403 check" \
        '^nginx \[timeout -k 10 30\] -t -q -c /opt/minerbot/state/nginx-check/nginx.conf$' \
        '^nginx \[timeout -k 10 30\] -t -q$' '^restartsrv_nginx \[timeout -k 10 120\] --reload$' \
        '^curl \[timeout -k 10 15\] -sk --max-time 10 --resolve bot.murderminershub.com:443:127.0.0.1 '
    [ "$(unit_calls | grep -c '^restartsrv_nginx ')" = 1 ] || problem "nginx wasn't reloaded exactly once"
    [ "$(head -n 2 "$home/state/nginx-check/nginx.conf" 2> /dev/null)" = "include /etc/nginx/conf.d/modules/*.conf;
user nobody;" ] || problem "the standalone test's nginx.conf doesn't start with nginx.conf's main context" "$(head -n 4 "$home/state/nginx-check/nginx.conf")"
    expect_said "a request from this server gets 403" "the first setup"
    run_calls | args_of | grep -qF "curl [timeout -k 10 20] -fsS --max-time 15 --proto =https --max-redirs 0 https://myteam.cloudflareaccess.com/cdn-cgi/access/certs" \
        || problem "the Access team's keys weren't checked at myteam.cloudflareaccess.com"
    ! run_calls | args_of | grep -qE ' compose .* up ' || problem "the dashboard was started on a first setup"
    [ ! -e /fake/docker/ctr/minerbot-dashboard ] || problem "a dashboard container exists after the first setup"
    [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] \
        || problem "state/tmp isn't empty after the first setup, so a copy of the answers or the key may be left" "$(ls -A "$home/state/tmp")"
    while IFS= read -r line; do
        case $line in '#'* | '') continue ;; esac
        value=${line#*=}
        ( # shellcheck source=/dev/null
            source /repo/deploy/common.sh && env_safe "$value") || problem "${line%%=*} in dashboard.env breaks env_safe"
    done < "$home/dashboard.env"
    cp "$home/dashboard.env" /out/case13-dashboard.env
    verdict
}

case_14() {
    begin 14 "setup's nginx failures: a refused full test puts every file back byte for byte without a reload; a refused standalone test never touches conf.d; a cut-off change is undone first; a refused proxy-key rotation starts the dashboard again on its old settings; a failed smoke check still moves the env; --remove-nginx with nginx -t failing, with no image and no tables"
    local before env_before inode log first_nginx first_reload first_db
    nginx_accepts_now() { nginx_files > /fake/nginx/accepted; }

    # A refused full test
    restore setup
    echo "# A change, so nginx's files differ (made up)" >> "$home/minerbot-dashboard.conf"
    nginx_accepts_now
    before=$(fingerprint /etc/nginx)
    env_before=$(sha256sum < "$home/dashboard.env")
    in_home full-refused 120 env FAKE_NGINX_T=accepted ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "the refused full test"
    expect_said "nginx -t refused the new files (above), so the old ones are back and nginx wasn't reloaded." "the refused full test"
    same_state "the refused full test left nginx's files changed" "$before" "$(fingerprint /etc/nginx)"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "the refused full test reloaded nginx"
    [ ! -e "$home/state/nginx-change" ] || problem "the refused full test left the marker"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "the refused full test changed dashboard.env"

    # A refused standalone test
    restore setup
    echo "# A change, so nginx's files differ (made up)" >> "$home/minerbot-dashboard.conf"
    before=$(fingerprint /etc/nginx)
    inode=$(stat -c '%i %Y' /etc/nginx/conf.d/minerbot-dashboard.conf)
    in_home standalone-refused 120 env FAKE_NGINX_STANDALONE=fail ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "the refused standalone test"
    expect_said "nginx -t refused the new files on their own (above), before they went near nginx's folders." "the refused standalone test"
    same_state "the refused standalone test changed nginx's files" "$before" "$(fingerprint /etc/nginx)"
    [ "$(stat -c '%i %Y' /etc/nginx/conf.d/minerbot-dashboard.conf)" = "$inode" ] || problem "the conf.d file was touched"
    ! unit_calls | args_of | grep -qE '^nginx \[[^]]*\] -t -q$' || problem "the full test ran after the standalone test refused"

    # A cut-off change: the new certificate beside the old key, so nginx -t fails until it's undone
    restore setup
    install -d -m 700 "$home/state/nginx-backup/20261106T000000Z"
    cp -p /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/minerbot/origin-cert.pem /etc/nginx/minerbot/origin-key.pem \
        /etc/nginx/minerbot/proxy-key.conf "$home/state/nginx-backup/20261106T000000Z/"
    : > "$home/state/nginx-backup/20261106T000000Z/missing"
    : > "$home/state/nginx-backup/20261106T000000Z/complete"
    echo "$home/state/nginx-backup/20261106T000000Z" > "$home/state/nginx-change"
    before=$(fingerprint /etc/nginx)
    install -m 600 /work/certs/good2-cert.pem /etc/nginx/minerbot/origin-cert.pem
    in_home cut-off 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after a cut-off change"
    expect_said "An nginx change was cut off earlier. Its files are back as they were before it, and nginx reloaded." "setup after a cut-off change"
    same_state "the cut-off change wasn't undone byte for byte" "$before" "$(fingerprint /etc/nginx)"
    [ ! -e "$home/state/nginx-change" ] || problem "the marker is still there"
    first_nginx=$(unit_calls | grep -n -m 1 '^nginx ' | cut -d: -f1)
    first_reload=$(unit_calls | grep -n -m 1 '^restartsrv_nginx ' | cut -d: -f1)
    first_db=$(unit_calls | grep -n -m 1 '^mariadb ' | cut -d: -f1)
    [ -n "$first_nginx" ] && [ -n "$first_reload" ] && [ -n "$first_db" ] && [ "$first_nginx" -lt "$first_reload" ] \
        && [ "$first_reload" -lt "$first_db" ] || problem "the cut-off change wasn't undone (tested, reloaded) before anything else" \
        "$(unit_calls | args_of | head -n 12)"

    # --rotate-proxy-key with a refused full test, while the dashboard runs
    restore deployed
    nginx_accepts_now
    env_before=$(sha256sum < "$home/dashboard.env")
    in_home rotate-refused 120 env FAKE_NGINX_T=accepted ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the refused key rotation"
    expect_said "nginx refused the new files, so nothing changed, and the dashboard runs again on its old settings." "the refused key rotation"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "dashboard.env changed"
    [ "$(cat /etc/nginx/minerbot/proxy-key.conf)" = "proxy_set_header X-MinerBot-Proxy-Key \"$key1\";" ] || problem "nginx's key changed"
    [ ! -e "$home/dashboard.env.next" ] || problem "dashboard.env.next is still there"
    in_order "the dashboard stopped, then started again" ' compose .* stop dashboard$' '^nginx ' \
        ' compose .* up -d --no-build --no-deps dashboard$'
    read -r _ log _ < /fake/docker/ctr/minerbot-dashboard
    [ "$log" = true ] || problem "the dashboard isn't running again"
    expect_said "The dashboard is ready" "the refused key rotation"

    # A smoke check that fails after a good reload
    restore deployed
    in_home smoke 120 env FAKE_LOCAL_ANSWER=200 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the failed smoke check"
    expect_said "Problem: nginx runs the new file, but it answered 200 to a request from this server" "the failed smoke check"
    log=$(sed -n 's/^Dashboard__ProxyKey=//p' "$home/dashboard.env")
    [ "$log" != "$key1" ] && [ "$(cat /etc/nginx/minerbot/proxy-key.conf)" = "proxy_set_header X-MinerBot-Proxy-Key \"$log\";" ] \
        || problem "the env didn't move with nginx's new key"
    run_calls | args_of | grep -qE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$' || problem "the dashboard wasn't recreated"

    # --remove-nginx while nginx -t fails, with no dashboard image and no tables: the test passes without the file
    restore setup
    rm -f /fake/docker/tags/minerbot-dashboard@*
    { echo absent; nginx_files | tail -n 3; } > /fake/nginx/accepted
    before=$(sha256sum < /etc/nginx/conf.d/minerbot-dashboard.conf)
    in_home remove 120 env FAKE_NGINX_T=accepted FAKE_DB_TABLES=none ./setup-dashboard.sh --remove-nginx --yes
    expect_exit 0 "--remove-nginx"
    expect_said "MinerBot's file is out of nginx, and nginx reloaded." "--remove-nginx"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] || problem "--remove-nginx left the conf.d file"
    [ "$(find "$home/state/nginx-backup" -name minerbot-dashboard.conf -exec sha256sum {} \; | cut -c1-64 | head -n 1)" = "${before:0:64}" ] \
        || problem "--remove-nginx kept no copy of the file"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 1 ] || problem "--remove-nginx didn't reload exactly once"
    [ ! -e "$home/state/nginx-change" ] || problem "--remove-nginx left the marker"
    [ -d /etc/nginx/minerbot ] || problem "--remove-nginx removed /etc/nginx/minerbot"
    ! run_calls | grep -qE '^(mariadb|docker) ' || problem "--remove-nginx ran the image or database checks"

    # --remove-nginx when nginx -t still fails without the file
    restore setup
    before=$(fingerprint /etc/nginx)
    in_home remove-other 120 env FAKE_NGINX_T=fail ./setup-dashboard.sh --remove-nginx --yes
    expect_exit 1 "--remove-nginx with another broken file"
    expect_said "the problem is in another file" "--remove-nginx with another broken file"
    same_state "--remove-nginx didn't put the file back byte for byte" "$before" "$(fingerprint /etc/nginx)"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "--remove-nginx reloaded with another broken file"
    [ ! -e "$home/state/nginx-change" ] || problem "--remove-nginx with another broken file left the marker"

    # --remove-nginx without the file
    restore setup
    rm -f /etc/nginx/conf.d/minerbot-dashboard.conf
    in_home remove-absent 60 ./setup-dashboard.sh --remove-nginx --yes
    expect_exit 0 "--remove-nginx without the file"
    expect_said "MinerBot's file isn't in nginx. Nothing was changed." "--remove-nginx without the file"
    verdict
}

case_15() {
    begin 15 "setup converges: Enter and keep change nothing and only add an ok line, and clear what a cut-off run left in state/tmp; an admin-list change only recreates; --rotate-proxy-key stops the dashboard before the reload and recreates it after; a wrong current password gets a new one in both places"
    local env_before nginx_before lines key pw
    restore deployed
    env_before=$(sha256sum < "$home/dashboard.env")
    nginx_before=$(fingerprint /etc/nginx)
    lines=$(wc -l < "$home/logs/history.log")
    # What a run cut off before its unit took the answers leaves
    install -d -m 700 "$home/state/tmp/setup.mbtest-left"
    : > "$home/state/tmp/setup.mbtest-left/answers"
    converse keep "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "keep"$'\r' \
        "MinerBot Dashboard's client ID" $'\r' \
        "!Its client secret" $'\r' \
        "The owners' Discord user IDs" $'\r' \
        "The admins' Discord user IDs" $'\r' \
        "The Cloudflare Access team domain" $'\r' \
        "Go ahead? Type yes" "yes"$'\r'
    expect_exit 0 "setup with Enter and keep"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "setup with Enter and keep reloaded nginx"
    ! run_calls | args_of | grep -qE ' compose .* (up|stop) ' || problem "setup with Enter and keep touched the dashboard"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "setup with Enter and keep changed dashboard.env"
    same_state "setup with Enter and keep changed nginx's files" "$nginx_before" "$(fingerprint /etc/nginx)"
    [ "$(($(wc -l < "$home/logs/history.log") - lines))" = 1 ] && tail -n 1 "$home/logs/history.log" | grep -qP '\tsetup\tdashboard\t-\tok\tno change(\t|$)' \
        || problem "history.log didn't get exactly one 'ok no change' line" "$(tail -n 3 "$home/logs/history.log")"
    [ ! -e "$home/state/tmp/setup.mbtest-left" ] || problem "what a cut-off run left in state/tmp is still there"

    restore deployed
    nginx_before=$(fingerprint /etc/nginx)
    converse admins "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "keep"$'\r' \
        "MinerBot Dashboard's client ID" $'\r' \
        "!Its client secret" $'\r' \
        "The owners' Discord user IDs" $'\r' \
        "The admins' Discord user IDs" "100000000000000003"$'\r' \
        "The Cloudflare Access team domain" $'\r' \
        "Go ahead? Type yes" "yes"$'\r'
    expect_exit 0 "the admin-list change"
    grep -qx 'Dashboard__Admins=100000000000000003' "$home/dashboard.env" || problem "the admins weren't changed"
    [ "$(grep -v '^Dashboard__Admins=' "$home/dashboard.env")" = "$(grep -v '^Dashboard__Admins=' "$home/dashboard.env.previous" 2> /dev/null)" ] \
        || problem "the admin-list change changed other lines, or kept no dashboard.env.previous"
    same_state "the admin-list change touched nginx" "$nginx_before" "$(fingerprint /etc/nginx)"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "the admin-list change reloaded nginx"
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 1 ] \
        || problem "the dashboard wasn't recreated exactly once"

    restore deployed
    in_home rotate 120 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 0 "--rotate-proxy-key"
    in_order "--rotate-proxy-key: stop, reload, recreate" ' compose .* stop dashboard$' '^restartsrv_nginx ' \
        ' compose .* up -d --no-build --no-deps --force-recreate dashboard$'
    key=$(sed -n 's/^Dashboard__ProxyKey=//p' "$home/dashboard.env")
    [ "$key" != "$key1" ] && [ "$(cat /etc/nginx/minerbot/proxy-key.conf)" = "proxy_set_header X-MinerBot-Proxy-Key \"$key\";" ] \
        || problem "the new proxy key isn't in both places"

    restore deployed
    echo "AnotherPasswordSetByHand0123456789abcdef" > /fake/db/password
    in_home password 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with a wrong current password"
    pw=$(sed -n 's/^ConnectionStrings__BotConnection=.*;Pwd=\([^;]*\);.*/\1/p' "$home/dashboard.env")
    [ "$pw" != "$pw1" ] && [ "$pw" = "$(cat /fake/db/password)" ] \
        && [ "$(sed -n 's/^ConnectionStrings__DefaultConnection=.*;Pwd=\([^;]*\);.*/\1/p' "$home/dashboard.env")" = "$pw" ] \
        || problem "the new password isn't in MariaDB and both connection strings"
    run_calls | args_of | grep -qE '^mariadb \[[^]]*\] --batch --skip-print-query-on-error \{stdin: login\}$' \
        || problem "the password wasn't changed through stdin with --skip-print-query-on-error"
    verdict
}

case_16() {
    begin 16 "setup refuses, each with its message: a wildcard certificate, a two-name one, one ending within 30 days, a key that doesn't match, an encrypted key, a bad client ID, a secret with \$, ' #' or a space at the end, an ID on both lists, a bad AUD; and changes nothing, nor leaves its answers in state/tmp"
    local before mark
    restore deployed
    before=$(home_state)
    mark=$(wc -l < "$calls")
    converse certificates "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "$(paste_of /work/certs/wildcard-cert.pem)"$'\n' \
        "!Paste the Origin certificate" "$(paste_of /work/certs/twoname-cert.pem)"$'\n' \
        "!Paste the Origin certificate" "$(paste_of /work/certs/short-cert.pem)"$'\n'
    expect_exit 1 "three wrong certificates"
    [ "$(grep -c 'That certificate must name bot.murderminershub.com and nothing else' "$last")" = 2 ] \
        || problem "the wildcard and two-name certificates weren't both refused for their names" "$(excerpt)"
    expect_said "That certificate ends within 30 days." "the short certificate"
    expect_said "Three answers in a row weren't right. Nothing was changed." "three wrong certificates"

    # shellcheck disable=SC2016 # a $ the secret must not have
    converse keys "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "$(paste_of /work/certs/good-cert.pem)"$'\n' \
        "!Paste its private key" "$(paste_of /work/certs/good2-key.pem)"$'\n' \
        "!Paste its private key" "$(paste_of /work/certs/encrypted-key.pem)"$'\n' \
        "!Paste its private key" "$(paste_of /work/certs/good-key.pem)"$'\n' \
        "MinerBot Dashboard's client ID" "12345"$'\r' \
        "MinerBot Dashboard's client ID" $'\r' \
        "!Its client secret" 'bad$value0123456789abc'$'\r' \
        "!Its client secret" 'bad #value0123456789abc'$'\r' \
        "!Its client secret" 'trailing0123456789abc '$'\r'
    expect_exit 1 "the wrong key and secrets"
    expect_said "That private key doesn't belong to the certificate." "the key that doesn't match"
    expect_said "openssl can't parse that as a private key without a passphrase." "the encrypted key"
    expect_said "The client ID is 17 to 20 digits" "the bad client ID"
    [ "$(grep -c "That can't go into an env file" "$last")" = 3 ] || problem "the three bad secrets weren't all refused for env_safe" "$(excerpt)"
    expect_said "Three answers in a row weren't right. Nothing was changed." "the bad secrets"

    converse lists "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "keep"$'\r' \
        "MinerBot Dashboard's client ID" $'\r' \
        "!Its client secret" $'\r' \
        "The owners' Discord user IDs" $'\r' \
        "The admins' Discord user IDs" "100000000000000001"$'\r' \
        "The admins' Discord user IDs" "none"$'\r' \
        "The Cloudflare Access team domain" "myteam"$'\r' \
        "The Access application's AUD tag" "xyz"$'\r' \
        "The Access application's AUD tag" "$aud"$'\r' \
        "Go ahead? Type yes" "no"$'\r'
    expect_exit 1 "the answer no"
    expect_said "100000000000000001 is on both lists. Put each person on one list only." "the ID on both lists"
    expect_said "The AUD tag is 64 characters" "the bad AUD"
    expect_said "Stopped. Nothing was changed." "the answer no"
    same_state "the refused setups changed something" "$before" "$(home_state)"
    ! tail -n +"$((mark + 1))" "$calls" | grep -q '^systemd-run ' || problem "a refused setup started its unit"
    [ -z "$(find "$home/state/tmp" -mindepth 1 -maxdepth 1 -name 'setup.*' 2> /dev/null)" ] \
        || problem "a refused setup left its answers in state/tmp" "$(ls -A "$home/state/tmp")"
    verdict
}

# ---- More of setup's cases, 50 to 59

case_50() {
    begin 50 "setup applies the rights file the last apply used, from the copy apply_rights keeps: a newer dashboard-rights.sql, as a --only bot deploy leaves it, isn't applied, and the summary and the log say which file; only with no copy yet, as on a first setup, is the installed file applied, and that apply keeps its copy"
    local marker='-- mbtest: a newer rights file, which a --only bot deploy installed without applying it'
    restore deployed
    # The last apply used the repository's file; a later commit's file is installed, as a --only bot deploy leaves it
    install -m 600 /repo/deploy/dashboard-rights.sql "$home/state/dashboard-rights.applied.sql"
    printf '%s\n' "$marker" >> "$home/dashboard-rights.sql"
    rm -f /fake/db/applied.sql
    in_home copy 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after a newer rights file was installed"
    expect_said "apply the database rights the last apply used again, from state/dashboard-rights.applied.sql" "the summary"
    expect_said "Applying the rights the last apply used, from state/dashboard-rights.applied.sql" "setup after a newer rights file was installed"
    if [ ! -f /fake/db/applied.sql ]; then
        problem "setup applied no rights file"
    elif grep -qF -- "$marker" /fake/db/applied.sql; then
        problem "setup applied the newer dashboard-rights.sql, which no dashboard deploy applied"
    fi
    cmp -s /repo/deploy/dashboard-rights.sql "$home/state/dashboard-rights.applied.sql" \
        || problem "the copy of the rights file the last apply used changed"

    # No copy yet, as on a first setup
    rm -f "$home/state/dashboard-rights.applied.sql" /fake/db/applied.sql
    in_home first 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with no copy yet"
    expect_said "apply the database rights in dashboard-rights.sql" "the summary with no copy yet"
    expect_said "Applying the rights in dashboard-rights.sql: no apply has kept a copy of its file yet." "setup with no copy yet"
    grep -qF -- "$marker" /fake/db/applied.sql 2> /dev/null || problem "with no copy yet, setup didn't apply the installed dashboard-rights.sql"
    cmp -s "$home/dashboard-rights.sql" "$home/state/dashboard-rights.applied.sql" 2> /dev/null \
        && [ "$(stat -c %a "$home/state/dashboard-rights.applied.sql")" = 600 ] \
        || problem "the apply didn't keep a 600 copy of the file it applied"
    verdict
}

case_51() {
    begin 51 "--rotate-db-password runs that stop: a rights failure stops them before the password changes, so MariaDB, dashboard.env and the dashboard keep the old one; a stop after the change gives dashboard.env the new password and recreates the dashboard; a stop after nginx reloaded with a new proxy key moves the whole candidate"
    local env_before fake pw key running
    for fake in FAKE_REHEARSAL=fail FAKE_APPLY=fail FAKE_RIGHTS_EXTRA=1; do
        restore deployed
        env_before=$(sha256sum < "$home/dashboard.env")
        in_home "rights-${fake%%=*}" 120 env "$fake" ./setup-dashboard.sh --no-questions --yes --rotate-db-password
        expect_exit 1 "the rotation with $fake"
        expect_said "The dashboard's database rights didn't apply (above), and its password is as it was." "the rotation with $fake"
        [ "$(cat /fake/db/password 2> /dev/null)" = "$pw1" ] || problem "with $fake, the login's password changed, though its rights didn't apply"
        [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "with $fake, dashboard.env changed"
        ! run_calls | grep -qF '{stdin: login}' || problem "with $fake, the password changed before the rights applied"
        ! run_calls | args_of | grep -qE ' compose .* (up|stop) ' || problem "with $fake, the dashboard was stopped or recreated"
    done

    # A stop after the password changed: MariaDB refuses the login on the website's database
    restore deployed
    in_home password-test 120 env FAKE_DB_DENY=website_db ./setup-dashboard.sh --no-questions --yes --rotate-db-password
    expect_exit 1 "the rotation whose password test fails"
    expect_said "The dashboard's login can't open both databases with its password (above)." "the rotation whose password test fails"
    expect_said "The dashboard's database password changed in this run, so dashboard.env got the new one and nothing else." \
        "the rotation whose password test fails"
    pw=$(cat /fake/db/password 2> /dev/null)
    [ -n "$pw" ] && [ "$pw" != "$pw1" ] && [ "$(grep -c "^ConnectionStrings__[A-Za-z]*=.*;Pwd=$pw;" "$home/dashboard.env")" = 2 ] \
        || problem "MariaDB and both of dashboard.env's connection strings don't have the same new password"
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 1 ] \
        || problem "the dashboard wasn't recreated once, with the new password"
    read -r _ running _ < /fake/docker/ctr/minerbot-dashboard
    [ "$running" = true ] || problem "the dashboard doesn't run after the rotation that stopped"

    # A stop after nginx reloaded with a new proxy key, before dashboard.env moved: a folder named dashboard.env inside
    # dashboard.env.previous makes that move fail
    restore deployed
    mkdir -p "$home/dashboard.env.previous/dashboard.env"
    in_home key-reloaded 120 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the key rotation that stopped after nginx's reload"
    expect_said "nginx runs the new files already, so dashboard.env got its new settings too." "the key rotation that stopped after nginx's reload"
    key=$(sed -n 's/^Dashboard__ProxyKey=//p' "$home/dashboard.env")
    [ -n "$key" ] && [ "$key" != "$key1" ] \
        && [ "$(cat /etc/nginx/minerbot/proxy-key.conf)" = "proxy_set_header X-MinerBot-Proxy-Key \"$key\";" ] \
        || problem "dashboard.env's proxy key isn't the one nginx sends now"
    run_calls | args_of | grep -qE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$' \
        || problem "the dashboard wasn't recreated with the new proxy key"
    read -r _ running _ < /fake/docker/ctr/minerbot-dashboard
    [ "$running" = true ] || problem "the dashboard doesn't run after the key rotation that stopped"
    verdict
}

case_52() {
    begin 52 "--rotate-db-password gives the login a new password after its rights, through stdin, in MariaDB and both connection strings, and recreates the dashboard once, nginx untouched; when the dashboard's own check refuses the new settings, the run stops before the login, its rights, dashboard.env, nginx or the dashboard change"
    local pw nginx_before env_before
    restore deployed
    nginx_before=$(fingerprint /etc/nginx)
    in_home rotate 120 ./setup-dashboard.sh --no-questions --yes --rotate-db-password
    expect_exit 0 "--rotate-db-password"
    pw=$(cat /fake/db/password 2> /dev/null)
    [ -n "$pw" ] && [ "$pw" != "$pw1" ] && [ "$(grep -c "^ConnectionStrings__[A-Za-z]*=.*;Pwd=$pw;" "$home/dashboard.env")" = 2 ] \
        || problem "the new password isn't in MariaDB and both connection strings"
    run_calls | args_of | grep -qE '^mariadb \[timeout -k 10 30\] --batch --skip-print-query-on-error \{stdin: login\}$' \
        || problem "the password wasn't changed through stdin with --skip-print-query-on-error"
    in_order "--rotate-db-password: the rights, then the new password, then the recreate" '\{stdin: apply\}' '\{stdin: login\}' \
        ' compose .* up -d --no-build --no-deps --force-recreate dashboard$'
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 1 ] \
        || problem "the dashboard wasn't recreated exactly once"
    same_state "--rotate-db-password changed nginx's files" "$nginx_before" "$(fingerprint /etc/nginx)"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "--rotate-db-password reloaded nginx"

    restore deployed
    nginx_before=$(fingerprint /etc/nginx)
    env_before=$(sha256sum < "$home/dashboard.env")
    in_home refused 120 env FAKE_CHECK_SETTINGS=fail ./setup-dashboard.sh --no-questions --yes --rotate-db-password
    expect_exit 1 "the rotation the dashboard's own check refuses"
    expect_said "The dashboard's own check refused the new settings (above). Nothing was changed." "the rotation the dashboard's own check refuses"
    ! run_calls | grep -qE '\{stdin: (login|rehearsal|apply)\}' || problem "the login or its rights changed after the dashboard's own check refused"
    [ "$(cat /fake/db/password 2> /dev/null)" = "$pw1" ] || problem "the login's password changed after the dashboard's own check refused"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "dashboard.env changed after the dashboard's own check refused"
    same_state "nginx's files changed after the dashboard's own check refused" "$nginx_before" "$(fingerprint /etc/nginx)"
    ! run_calls | args_of | grep -qE ' compose .* (up|stop) ' || problem "the dashboard was stopped or recreated after its own check refused"
    verdict
}

case_53() {
    begin 53 "a private key pasted at the certificate prompt never reaches the screen, alone or after the certificate in one paste, and setup says what it was; that first setup still installs the right certificate and key; paste_pem drains what's left of a hidden paste before echo comes back"
    local cert key
    restore built
    cert=$(paste_of /work/certs/good-cert.pem)$'\n'
    key=$(paste_of /work/certs/good-key.pem)$'\n'
    converse key-first "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "$key" \
        "!Paste the Origin certificate" "$cert$key" \
        "!Paste its private key" "$key" \
        "MinerBot Dashboard's client ID" "$client_id"$'\r' \
        "!Its client secret" "$secret1"$'\r' \
        "The owners' Discord user IDs" "$owners"$'\r' \
        "The admins' Discord user IDs" "none"$'\r' \
        "The Cloudflare Access team domain" "none"$'\r' \
        "Go ahead? Type yes" "yes"$'\r'
    expect_exit 0 "the first setup with a key pasted at the certificate prompt"
    expect_said "That was a private key, and it wasn't shown." "the key pasted at the certificate prompt"
    ! grep -qFf /work/certs/good-key.pem "$last" || problem "a line of the private key is in the transcript"
    ! said "$(sed -n 2p /work/certs/good-cert.pem)" || problem "the certificate's paste is in the transcript"
    expect_said "The certificate names $host_name only." "the certificate pasted with its key"
    cmp -s /etc/nginx/minerbot/origin-cert.pem /work/certs/good-cert.pem && cmp -s /etc/nginx/minerbot/origin-key.pem /work/certs/good-key.pem \
        || problem "nginx didn't get the pasted certificate and key"

    # The order inside paste_pem: what's left of a hidden paste is drained while echo is still off
    cat > /tmp/mbtest-drain.sh << 'EOF'
source /repo/deploy/common.sh
eval "real_$(declare -f drain_input)"
drain_input() {
    if stty -a | tr ' ;' '\n\n' | grep -qx -- -echo; then echo "drained with echo off"; else echo "drained with echo on"; fi
    real_drain_input
}
echo "Paste now."
paste_pem "PRIVATE KEY" /tmp/mbtest-drain.pem hidden
echo "paste_pem returned $?"
EOF
    converse drain "bash /tmp/mbtest-drain.sh" "!Paste now." "not a key"$'\r\n'"the rest of the paste"$'\r\n'
    expect_said "paste_pem returned 6" "paste_pem with a wrong first line"
    expect_said "drained with echo off" "paste_pem's drain"
    verdict
}

case_54() {
    begin 54 "setup's unit when the dashboard runs into trouble: an admin-list change the dashboard isn't ready with puts the earlier dashboard.env back; a stop after the run stopped the dashboard starts it again on its old settings; the unit holds the lock while it runs"
    local before running bg log seen n
    restore deployed
    cp -p "$home/dashboard.env" /tmp/mbtest-dashboard.env
    converse unready "env MINERBOT_TEST_READ_LIMIT=30 FAKE_UNREADY=dashboard@${c1:0:12} $home/setup-dashboard.sh" \
        "!Paste the Origin certificate" "keep"$'\r' \
        "MinerBot Dashboard's client ID" $'\r' \
        "!Its client secret" $'\r' \
        "The owners' Discord user IDs" $'\r' \
        "The admins' Discord user IDs" "100000000000000003"$'\r' \
        "The Cloudflare Access team domain" $'\r' \
        "Go ahead? Type yes" "yes"$'\r'
    expect_exit 1 "the admin-list change the dashboard isn't ready with"
    expect_said "The dashboard wasn't ready with the new settings, so the earlier ones go back." "the admin-list change the dashboard isn't ready with"
    cmp -s "$home/dashboard.env" /tmp/mbtest-dashboard.env || problem "dashboard.env isn't back to its earlier bytes"
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 2 ] \
        || problem "the dashboard wasn't recreated a second time, on the earlier settings"

    # A stop after the run stopped the dashboard: a file where nginx_change makes its check folder
    restore deployed
    before=$(sha256sum < "$home/dashboard.env")
    : > "$home/state/nginx-check"
    in_home stopped 120 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the key rotation that stopped after stopping the dashboard"
    expect_said "The dashboard runs again on its old settings." "the key rotation that stopped after stopping the dashboard"
    in_order "the dashboard stopped, then started again" ' compose .* stop dashboard$' ' compose .* up -d --no-build --no-deps dashboard$'
    read -r _ running _ < /fake/docker/ctr/minerbot-dashboard
    [ "$running" = true ] || problem "the dashboard doesn't run again"
    [ "$(sha256sum < "$home/dashboard.env")" = "$before" ] || problem "dashboard.env changed"

    # The lock, while the unit waits for a dashboard that never gets ready
    restore deployed
    ( in_home locked 120 env FAKE_UNREADY="dashboard@${c1:0:12}" ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key ) &
    bg=$!
    seen=no
    log=''
    for n in $(seq 1 150); do
        log=$(last_log setup)
        if [ -n "$log" ] && grep -q 'Recreating the dashboard' "$log" 2> /dev/null; then
            seen=yes
            break
        fi
        sleep 0.1
    done
    if [ "$seen" = no ]; then
        problem "setup's unit didn't get to recreating the dashboard within 15 seconds (after $n looks)" "$(excerpt "$log")"
    else
        ! flock -n "$home/state/lock" true || problem "nothing held the lock while setup's unit ran"
        grep -q '^setup, started ' "$home/state/lock.info" 2> /dev/null \
            || problem "lock.info doesn't name setup while its unit runs" "$(cat "$home/state/lock.info" 2> /dev/null)"
    fi
    wait "$bg"
    verdict
}

case_55() {
    begin 55 "setup refuses before any question, each with its reason, and changes nothing: no dashboard tables, the website's three tables not all there, --apply with another option, no dashboard image"
    local before
    restore deployed
    before=$(home_state)
    refused() {   # refused <name> <text> <command...>
        local name=$1 text=$2
        shift 2
        in_home "$name" 60 "$@"
        expect_exit 1 "$name"
        expect_said "$text" "$name"
        ! run_calls | grep -q '^systemd-run ' || problem "$name: it started its unit"
    }
    refused no-tables "MinerBot's database has no dashboard tables yet." env FAKE_DB_TABLES=none ./setup-dashboard.sh --no-questions --yes
    refused site-two "The website's database, SITE_DB in server.conf, doesn't hold the armor, blacklist and Discord server tables." \
        env FAKE_DB_TABLES=site-two ./setup-dashboard.sh --no-questions --yes
    refused apply-yes "--apply goes alone" ./setup-dashboard.sh --yes --apply "$home/state/tmp/setup.mbtest"
    rm -f /fake/docker/tags/minerbot-dashboard@*
    refused no-image "There's no dashboard image yet." ./setup-dashboard.sh --no-questions --yes
    same_state "a refused setup changed something" "$before" "$(home_state)"
    verdict
}

case_56() {
    begin 56 "the env-file helpers on their own: env_safe refuses a tab, an accented letter and DEL, and takes setup's own connection string; env_value takes the last NAME= line, as Compose does"
    local i
    local -a names=("a tab" "an accented letter" "a DEL") values=($'a\tb' $'caf\xc3\xa9' $'a\x7fb')
    # shellcheck source=/dev/null
    in_common() { (source /repo/deploy/common.sh && "$@") 2> /dev/null; }
    for i in 0 1 2; do
        ! in_common env_safe "${values[$i]}" || problem "env_safe takes a value with ${names[$i]}"
    done
    in_common env_safe "Server=127.0.0.1;Port=3306;Database=minerbot_db;Uid=minerbot-dashboard;Pwd=Mbtest0123456789;SslMode=Disabled;Connection Timeout=15;CharSet=utf8mb4;Maximum Pool Size=6;" \
        || problem "env_safe refuses setup's own connection string"
    printf '%s\n' "ConnectionStrings__BotConnection=Server=first" "BOTTOKEN=made-up" "ConnectionStrings__BotConnection=Server=last" \
        > /tmp/mbtest-env
    [ "$(in_common env_value /tmp/mbtest-env ConnectionStrings__BotConnection)" = Server=last ] \
        || problem "env_value didn't take the last ConnectionStrings__BotConnection= line"
    verdict
}

dashboard_runs() {   # the dashboard's container's running field, as the stand-in docker keeps it: true, false or nothing
    local _ running=''
    { read -r _ running _ < /fake/docker/ctr/minerbot-dashboard; } 2> /dev/null || true
    printf '%s\n' "$running"
}

stop_by_hand() {   # the dashboard's container as docker compose stop dashboard leaves it
    local image running restarts started health pid cid hash
    read -r image running restarts started health pid cid hash < /fake/docker/ctr/minerbot-dashboard
    printf '%s false %s %s %s 0 %s %s\n' "$image" "$restarts" "$started" "$health" "$cid" "$hash" \
        > /fake/docker/ctr/minerbot-dashboard
}

set_ctr_image() {   # set_ctr_image <image ID>: the dashboard's container runs that image, as a deploy or rollback leaves it
    local image running restarts started health pid cid hash
    read -r image running restarts started health pid cid hash < /fake/docker/ctr/minerbot-dashboard
    printf '%s %s %s %s %s %s %s %s\n' "$1" "$running" "$restarts" "$started" "$health" "$pid" "$cid" "$hash" \
        > /fake/docker/ctr/minerbot-dashboard
}

env_key() { sed -n 's/^Dashboard__ProxyKey=//p' "$home/dashboard.env"; }
sent_key() { sed -n 's/^proxy_set_header X-MinerBot-Proxy-Key "\([0-9a-f]\{64\}\)";$/\1/p' /etc/nginx/minerbot/proxy-key.conf; }

cut_setup() {   # cut_setup <step>: setup --rotate-proxy-key, its unit killed after that part of its step 8, as a reboot would
    in_home "cut-$1" 120 env MINERBOT_TEST_CUT_AFTER="$1" ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the rotation cut off after step $1"
    expect_said "Cut off after step $1 (MINERBOT_TEST_CUT_AFTER)." "the rotation cut off after step $1" "$(last_log setup)"
    [ "$(dashboard_runs)" = false ] || problem "the rotation cut off after step $1 didn't leave the dashboard stopped"
}

case_57() {
    begin 57 "setup never leaves a dashboard stopped whose container exists: stopped by hand with nothing else to change, it's started, said and logged, and then left alone; a --rotate-proxy-key cut off after the stop, during nginx's change, after it (where nginx's new key stays and dashboard.env takes it) and after dashboard.env moved is finished by the plain run with the dashboard recreated; a stop call that hits its limit after Docker stopped it starts it again, and one that fails while it runs leaves it ready, earlier restarts and all; a rotation whose recreate fails, and a refused one whose start fails, are mended by the run their messages advise"
    local env_before nginx_before lines key backup=$home/state/nginx-backup/20261106T000000Z
    restore deployed
    stop_by_hand
    env_before=$(sha256sum < "$home/dashboard.env")
    nginx_before=$(nginx_files)
    lines=$(wc -l < "$home/logs/history.log")
    in_home by-hand 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with the dashboard stopped by hand"
    expect_said "start the dashboard, which isn't running" "the summary with the dashboard stopped by hand"
    expect_said "Done. What changed: dashboard started." "setup with the dashboard stopped by hand"
    [ "$(dashboard_runs)" = true ] || problem "setup with the dashboard stopped by hand left it stopped"
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 1 ] \
        || problem "the dashboard wasn't recreated, and so started, exactly once"
    ! run_calls | args_of | grep -qE ' compose .* stop ' || problem "setup with the dashboard stopped by hand stopped it"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "setup with the dashboard stopped by hand changed dashboard.env"
    same_state "setup with the dashboard stopped by hand changed nginx's files" "$nginx_before" "$(nginx_files)"
    [ "$(($(wc -l < "$home/logs/history.log") - lines))" = 1 ] \
        && tail -n 1 "$home/logs/history.log" | grep -qP '\tsetup\tdashboard\t-\tok\tdashboard started(\t|$)' \
        || problem "history.log didn't get exactly one 'ok dashboard started' line" "$(tail -n 2 "$home/logs/history.log")"
    in_home running 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with the dashboard running again"
    ! run_calls | args_of | grep -qE ' compose .* (up|stop) ' || problem "setup with the dashboard running again touched it"

    restore deployed
    cut_setup 8-stop
    in_home after-stop 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after the rotation cut off after the stop"
    expect_said "What changed: dashboard started." "setup after the rotation cut off after the stop"
    [ "$(dashboard_runs)" = true ] || problem "setup after the rotation cut off after the stop left the dashboard stopped"

    # Cut off during nginx's change: the marker and a complete backup, nginx on another key, the dashboard stopped
    restore deployed
    stop_by_hand
    install -d -m 700 "$backup"
    cp -p /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/minerbot/origin-cert.pem /etc/nginx/minerbot/origin-key.pem \
        /etc/nginx/minerbot/proxy-key.conf "$backup/"
    : > "$backup/missing"
    : > "$backup/complete"
    echo "$backup" > "$home/state/nginx-change"
    nginx_before=$(nginx_files)
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$(printf 'mbtest proxy key two' | sha256sum | cut -c1-64)" \
        > /etc/nginx/minerbot/proxy-key.conf
    in_home during-nginx 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after the rotation cut off during nginx's change"
    expect_said "What changed: cut-off nginx change undone, dashboard started." "setup after the rotation cut off during nginx's change"
    same_state "the cut-off change wasn't undone byte for byte" "$nginx_before" "$(nginx_files)"
    [ "$(dashboard_runs)" = true ] || problem "setup after the rotation cut off during nginx's change left the dashboard stopped"

    restore deployed
    cut_setup 8-nginx
    key=$(sent_key)
    [ -n "$key" ] && [ "$key" != "$key1" ] && [ "$(env_key)" = "$key1" ] \
        || problem "the cut after nginx's change didn't leave nginx on a new key and dashboard.env on the old one"
    in_home after-nginx 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after the rotation cut off after nginx's change"
    expect_said "give dashboard.env the proxy key nginx sends, which is newer" "the summary after the rotation cut off after nginx's change"
    expect_said "nginx sends a newer proxy key than dashboard.env has" "setup after the rotation cut off after nginx's change"
    [ "$(sent_key)" = "$key" ] || problem "setup after the rotation cut off after nginx's change put the old proxy key back into nginx"
    [ "$(env_key)" = "$key" ] || problem "dashboard.env didn't take the newer proxy key nginx sends"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "setup after the rotation cut off after nginx's change changed nginx again"
    run_calls | args_of | grep -qE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$' \
        || problem "the dashboard wasn't recreated with the newer proxy key"
    [ "$(dashboard_runs)" = true ] || problem "setup after the rotation cut off after nginx's change left the dashboard stopped"

    # Cut off after dashboard.env moved: the container made before the move is recreated, so it reads the new key too
    restore deployed
    cut_setup 8-env
    key=$(env_key)
    [ "$key" != "$key1" ] && [ "$(sent_key)" = "$key" ] || problem "the cut after dashboard.env moved didn't leave the new key in both places"
    in_home after-env 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after the rotation cut off after dashboard.env moved"
    expect_said "What changed: dashboard started." "setup after the rotation cut off after dashboard.env moved"
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 1 ] \
        || problem "the dashboard wasn't recreated, so it could run on the dashboard.env it was made with"
    [ "$(dashboard_runs)" = true ] || problem "setup after the rotation cut off after dashboard.env moved left the dashboard stopped"

    restore deployed
    env_before=$(sha256sum < "$home/dashboard.env")
    nginx_before=$(nginx_files)
    in_home stop-limit 120 env FAKE_STOP=hang ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the rotation whose stop call hit its limit"
    expect_said "stopped while stopping the dashboard" "the rotation whose stop call hit its limit"
    expect_said "The dashboard runs again on its old settings." "the rotation whose stop call hit its limit"
    [ "$(dashboard_runs)" = true ] || problem "the rotation whose stop call hit its limit left the dashboard stopped"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "the rotation whose stop call hit its limit changed dashboard.env"
    same_state "the rotation whose stop call hit its limit changed nginx's files" "$nginx_before" "$(nginx_files)"
    # A stop call that fails with the container still running, which restarted once before this run: that restart isn't
    # this run's, so it's ready again as it runs
    restore deployed
    in_home stop-fails 120 env FAKE_STOP=fail FAKE_RESTARTS="dashboard@${c1:0:12}" ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the rotation whose stop call failed"
    expect_said "The dashboard runs again on its old settings." "the rotation whose stop call failed"
    [ "$(dashboard_runs)" = true ] || problem "the rotation whose stop call failed left the dashboard stopped"

    # The recreate with the new key fails: the run its message advises starts the dashboard
    restore deployed
    in_home recreate-fails 120 env FAKE_UP_FAIL=dashboard ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the rotation whose recreate failed"
    expect_said "Fix the cause, then run ./setup-dashboard.sh --no-questions." "the rotation whose recreate failed"
    [ "$(dashboard_runs)" = false ] || problem "the rotation whose recreate failed didn't leave the dashboard stopped"
    in_home after-recreate 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "the run the failed recreate's message advises"
    expect_said "What changed: dashboard started." "the run the failed recreate's message advises"
    [ "$(dashboard_runs)" = true ] || problem "the run the failed recreate's message advises left the dashboard stopped"

    restore deployed
    nginx_files > /fake/nginx/accepted
    in_home refused 120 env FAKE_NGINX_T=accepted FAKE_UP_FAIL=dashboard ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the refused rotation whose start failed"
    expect_said "Run ./setup-dashboard.sh --no-questions once the cause is fixed." "the refused rotation whose start failed"
    [ "$(dashboard_runs)" = false ] || problem "the refused rotation whose start failed didn't leave the dashboard stopped"
    in_home advised 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "the run the refused rotation's message advises"
    expect_said "What changed: dashboard started." "the run the refused rotation's message advises"
    [ "$(dashboard_runs)" = true ] || problem "the run the refused rotation's message advises left the dashboard stopped"
    verdict
}

case_58() {
    begin 58 "nginx's file moves only with the dashboard: while the installed reference copy is from another commit than the dashboard's image, as a --only bot or --no-start deploy leaves it, deploy.sh's note stays silent and setup keeps nginx's file and says why, through a key rotation too; once the dashboard runs that commit, the note shows and setup installs the file; after a rollback nginx's file stays; with no file in nginx yet, the reference goes in"
    local nginx_before conf_before key id
    # shellcheck source=/dev/null
    in_common() { (source /repo/deploy/common.sh && "$@") 2> /dev/null; }
    # A --only bot deploy of c5, which changed nginx's file, while the dashboard runs c1
    restore deployed
    echo "# c5's change to nginx's file (made up)" >> "$home/minerbot-dashboard.conf"
    echo "$c5" > "$home/state/deploy-files.commit"
    ! in_common nginx_file_differs || problem "deploy.sh's note would send the owner to setup while the dashboard still runs c1"
    nginx_before=$(nginx_files)
    in_home waits 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup while the reference copy waits for its dashboard"
    expect_said "keep nginx's current file for the dashboard: the reference copy is from ${c5:0:12}, but the dashboard's image is from ${c1:0:12}" \
        "the summary while the reference copy waits for its dashboard"
    expect_said "nginx keeps its current file for the dashboard" "setup while the reference copy waits for its dashboard"
    same_state "setup changed nginx's files while the reference copy waits for its dashboard" "$nginx_before" "$(nginx_files)"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "setup reloaded nginx while the reference copy waits for its dashboard"
    conf_before=$(sha256sum < /etc/nginx/conf.d/minerbot-dashboard.conf)
    in_home rotate 120 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 0 "--rotate-proxy-key while the reference copy waits for its dashboard"
    [ "$(sha256sum < /etc/nginx/conf.d/minerbot-dashboard.conf)" = "$conf_before" ] \
        || problem "--rotate-proxy-key put the reference copy that waits for its dashboard into nginx"
    key=$(env_key)
    [ "$key" != "$key1" ] && [ "$(sent_key)" = "$key" ] || problem "--rotate-proxy-key didn't put the new key in both places"

    # The dashboard of c5 deployed
    id=$(image_of "$c5" dashboard)
    echo "$c5" > "/fake/docker/ids/$id"
    echo "$id" > /fake/docker/tags/minerbot-dashboard@current
    set_ctr_image "$id"
    in_common nginx_file_differs || problem "deploy.sh's note wouldn't show once the dashboard runs c5"
    in_home due 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup once the dashboard runs c5"
    cmp -s /etc/nginx/conf.d/minerbot-dashboard.conf "$home/minerbot-dashboard.conf" \
        || problem "setup didn't put c5's nginx file into nginx once the dashboard runs c5"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 1 ] || problem "setup didn't reload nginx exactly once for c5's file"

    # A rollback to c1's dashboard
    id=$(image_of "$c1" dashboard)
    echo "$id" > /fake/docker/tags/minerbot-dashboard@current
    set_ctr_image "$id"
    nginx_before=$(nginx_files)
    in_home rolled-back 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup after a rollback"
    same_state "setup changed nginx's files after a rollback" "$nginx_before" "$(nginx_files)"

    # No file in nginx yet: the reference goes in, whatever its commit
    restore setup
    rm -f /etc/nginx/conf.d/minerbot-dashboard.conf
    echo "# c5's change to nginx's file (made up)" >> "$home/minerbot-dashboard.conf"
    echo "$c5" > "$home/state/deploy-files.commit"
    in_home first-file 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with no file in nginx yet"
    cmp -s /etc/nginx/conf.d/minerbot-dashboard.conf "$home/minerbot-dashboard.conf" \
        || problem "with no file in nginx yet, setup didn't put the reference copy in"
    verdict
}

case_59() {
    begin 59 "setup counts what it did only once it took: a recreate Compose reports done while the old container runs on is a failure, and the run its message advises recreates the dashboard, which runs on older settings than dashboard.env holds, keeps the compose.yml it's ready under, and then leaves it alone; a pipelog.conf or cPanel hook that isn't what setup writes is written again, with one reload; nginx's lone reload for an older copy of the certificate counts only once nginx took it; when cPanel's reload fails for the new files and the old, setup never says nothing changed, and the next run reloads the old files first"
    local cid pw nginx_before env_before hook=/etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog
    local pipelog=/etc/nginx/minerbot/pipelog.conf
    # The values Compose gave the container when it made it, which docker container inspect shows from then on
    made_with() { mkdir -p /fake/docker/env && grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$home/dashboard.env" > /fake/docker/env/minerbot-dashboard; }
    ctr_id() { local id; read -r _ _ _ _ _ _ id _ < /fake/docker/ctr/minerbot-dashboard; echo "$id"; }

    # A database password rotation whose recreate Compose reports done, while the old container runs on
    restore deployed
    made_with
    cid=$(ctr_id)
    in_home kept 120 env FAKE_UP_KEEP=dashboard ./setup-dashboard.sh --no-questions --yes --rotate-db-password
    expect_exit 1 "the rotation whose recreate Compose didn't do"
    expect_said "The dashboard isn't ready: Compose didn't make it a new container" "the rotation whose recreate Compose didn't do"
    ! said "Done." || problem "the rotation whose recreate Compose didn't do said it was done" "$(excerpt)"
    [ "$(ctr_id)" = "$cid" ] || problem "the stand-in made a new container, so the old one doesn't run on"
    pw=$(cat /fake/db/password 2> /dev/null)
    [ -n "$pw" ] && [ "$pw" != "$pw1" ] && grep -qF ";Pwd=$pw;" "$home/dashboard.env" \
        || problem "the rotation didn't leave the same new password in MariaDB and dashboard.env"
    # The run that message advises: the container still runs on the old password, though dashboard.env's works
    in_home advised 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "the run the message advises"
    expect_said "recreate the dashboard, which runs on other settings than dashboard.env holds" "the summary of the run the message advises"
    ! said "give dashboard.env the proxy key nginx sends" \
        || problem "the summary offered dashboard.env the proxy key nginx sends, which dashboard.env has already" "$(excerpt)"
    expect_said "What changed: dashboard recreated." "the run the message advises"
    [ "$(run_calls | args_of | grep -cE ' compose .* up -d --no-build --no-deps --force-recreate dashboard$')" = 1 ] \
        || problem "the run the message advises didn't recreate the dashboard exactly once"
    [ "$(ctr_id)" != "$cid" ] && grep -qF ";Pwd=$pw;" /fake/docker/env/minerbot-dashboard 2> /dev/null \
        || problem "after the run the message advises, the dashboard doesn't run on dashboard.env's password"
    cmp -s "$home/compose.yml" "$home/state/compose-ready-dashboard.yml" \
        || problem "the recreate that got ready didn't keep the compose.yml the dashboard is ready under"
    in_home settled 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "the run after that"
    ! run_calls | args_of | grep -qE ' compose .* (up|stop) ' || problem "the run after that touched the dashboard"

    # cPanel's hook gone, as on a server set up before it: the plain run writes it again, with one reload
    restore deployed
    ( # shellcheck source=/dev/null
        source /repo/deploy/common.sh && pipelog_hook_text) > /tmp/mbtest-hook
    rm -f "$hook"
    nginx_before=$(nginx_files)
    in_home no-hook 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup without cPanel's hook"
    [ "$(stat -c '%a %U' "$hook" 2> /dev/null)" = "755 root" ] && cmp -s "$hook" /tmp/mbtest-hook \
        || problem "setup didn't write cPanel's hook again, 755 and root's"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 1 ] || problem "setup without cPanel's hook didn't reload nginx exactly once"
    same_state "setup without cPanel's hook changed MinerBot's four files" "$nginx_before" "$(nginx_files)"
    # pipelog.conf emptied while cPanel loads the module, so cPanel's piped logs would get the dashboard's requests
    : > "$pipelog"
    in_home empty-pipelog 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with pipelog.conf empty while the module loads"
    [ "$(cat "$pipelog" 2> /dev/null)" = "pipelog off;" ] || problem "setup didn't write \"pipelog off;\" again while the module loads"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 1 ] || problem "setup with pipelog.conf empty didn't reload nginx exactly once"

    # nginx serves an older copy of the certificate: the lone reload counts only once nginx took it
    restore deployed
    nginx_workers "nginx: worker process"
    cp /work/certs/good2-cert.pem /fake/nginx/served-cert.pem
    in_home lone-not-taken 120 env FAKE_RELOAD=not-taken ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "the lone reload nginx didn't take"
    expect_said "reload nginx, which serves an older copy of the certificate" "the summary with an older certificate served"
    expect_said "cPanel's reload exited 0, but nginx kept its old worker processes" "the lone reload nginx didn't take"
    expect_said "Problem: nginx serves another certificate than the installed one, and the reload that would fix it failed, or nginx didn't take it (above)." \
        "the lone reload nginx didn't take"
    ! said "nginx reloaded: it served an older copy" || problem "the lone reload nginx didn't take was called done"
    in_home lone 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "the lone reload nginx took"
    expect_said "nginx reloaded: it served an older copy of the certificate." "the lone reload nginx took"
    cmp -s /fake/nginx/served-cert.pem /etc/nginx/minerbot/origin-cert.pem || problem "nginx doesn't serve the installed certificate after the lone reload"

    # cPanel's reload fails for the new files, and again for the old ones: nginx may run what the first one loaded, so
    # setup never says that nothing changed, and the marker stays for the next run, which reloads the old files first
    restore deployed
    env_before=$(sha256sum < "$home/dashboard.env")
    in_home reload-fails 120 env FAKE_RELOAD=fail ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the rotation whose reloads both failed"
    expect_said "WARNING: cPanel's reload failed again" "the rotation whose reloads both failed"
    expect_said "nginx refused the new files, and the old ones are back on disk, though nginx hasn't loaded them again (above), and the dashboard runs again on its old settings." \
        "the rotation whose reloads both failed"
    ! said "so nothing changed" || problem "the rotation whose reloads both failed said that nothing changed"
    [ -e "$home/state/nginx-change" ] || problem "the marker went, though nginx took no reload of the old files"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 2 ] || problem "cPanel's reload didn't run exactly twice: for the new files, then for the old"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "dashboard.env changed, though nginx's change was put back"
    in_home after-fails 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "the run after both reloads failed"
    expect_said "An nginx change was cut off earlier. Its files are back as they were before it, and nginx reloaded." "the run after both reloads failed"
    verdict
}

# ---- What a setup run that stops says it changed: 75

case_75() {
    begin 75 "a setup run that undid a cut-off nginx change, then stopped at the dashboard's own check, names that undo and says nothing else was changed, never that nothing was: nginx's files are back as they were before the cut-off change, nginx reloaded once, and the login, its rights, dashboard.env and the dashboard stay as they were; the same refusal with no cut-off change still says nothing was changed; a stop at server.conf's check of the databases right after the undo names it too"
    local backup=$home/state/nginx-backup/20261106T000000Z nginx_before env_before
    # Cut off during nginx's change, as in case 57: the marker, a complete backup, and nginx on another proxy key
    restore deployed
    install -d -m 700 "$backup"
    cp -p /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/minerbot/origin-cert.pem /etc/nginx/minerbot/origin-key.pem \
        /etc/nginx/minerbot/proxy-key.conf "$backup/"
    : > "$backup/missing"
    : > "$backup/complete"
    echo "$backup" > "$home/state/nginx-change"
    nginx_before=$(nginx_files)
    env_before=$(sha256sum < "$home/dashboard.env")
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$(printf 'mbtest proxy key two' | sha256sum | cut -c1-64)" \
        > /etc/nginx/minerbot/proxy-key.conf
    in_home undone-refused 120 env FAKE_CHECK_SETTINGS=fail ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "the run that undid a cut-off nginx change, then had its settings refused"
    expect_said "An nginx change was cut off earlier. Its files are back as they were before it, and nginx reloaded." \
        "the run that undid a cut-off nginx change, then had its settings refused"
    expect_said "The dashboard's own check refused the new settings (above). The cut-off nginx change was undone (above), and nothing else was changed." \
        "the run that undid a cut-off nginx change, then had its settings refused"
    ! said "Nothing was changed." \
        || problem "the run that undid a cut-off nginx change and reloaded nginx said that nothing was changed" "$(excerpt)"
    same_state "the cut-off change wasn't undone byte for byte" "$nginx_before" "$(nginx_files)"
    [ ! -e "$home/state/nginx-change" ] || problem "the cut-off change's marker is still there after its undo"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 1 ] || problem "the undo didn't reload nginx exactly once"
    ! run_calls | grep -qE '\{stdin: (login|rehearsal|apply)\}' || problem "the login or its rights changed after the dashboard's own check refused"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "dashboard.env changed after the dashboard's own check refused"
    ! run_calls | args_of | grep -qE ' compose .* (up|stop) ' || problem "the dashboard was stopped or recreated after its own check refused"

    # No cut-off change: the same refusal changed nothing, and says so
    restore deployed
    in_home refused 120 env FAKE_CHECK_SETTINGS=fail ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "the run whose settings the dashboard's own check refused"
    expect_said "The dashboard's own check refused the new settings (above). Nothing was changed." \
        "the run whose settings the dashboard's own check refused"
    ! said "nothing else was changed" || problem "the run with no cut-off change spoke of one" "$(excerpt)"

    # A stop in load_server_conf, right after the undo, names it too: MariaDB answers the foreground's checks, then not
    # the unit's count of the two databases. The stand-in mariadb gets a wrapper for this one run, and the case puts it
    # back however it ends.
    restore deployed
    install -d -m 700 "$backup"
    cp -p /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/minerbot/origin-cert.pem /etc/nginx/minerbot/origin-key.pem \
        /etc/nginx/minerbot/proxy-key.conf "$backup/"
    : > "$backup/missing"
    : > "$backup/complete"
    echo "$backup" > "$home/state/nginx-change"
    nginx_before=$(nginx_files)
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$(printf 'mbtest proxy key two' | sha256sum | cut -c1-64)" \
        > /etc/nginx/minerbot/proxy-key.conf
    trap 'if [ -e /usr/local/bin/mariadb.mbtest-75 ]; then mv -f /usr/local/bin/mariadb.mbtest-75 /usr/local/bin/mariadb; fi' EXIT
    if mv /usr/local/bin/mariadb /usr/local/bin/mariadb.mbtest-75; then
        cat > /usr/local/bin/mariadb << 'EOF'
#!/usr/bin/env bash
if [ "${MINERBOT_DETACHED:-}" = 1 ] && [[ $* == *information_schema.schemata* ]]; then
    echo "ERROR 2002 (HY000): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2)" >&2
    exit 1
fi
exec /usr/local/bin/mariadb.mbtest-75 "$@"
EOF
        chmod 755 /usr/local/bin/mariadb
        in_home undone-no-db 120 ./setup-dashboard.sh --no-questions --yes
        mv -f /usr/local/bin/mariadb.mbtest-75 /usr/local/bin/mariadb
        expect_exit 1 "the run that undid a cut-off nginx change, then lost MariaDB"
        expect_said "An nginx change was cut off earlier. Its files are back as they were before it, and nginx reloaded." \
            "the run that undid a cut-off nginx change, then lost MariaDB"
        expect_said "MariaDB didn't answer as root within 30 seconds (above). The cut-off nginx change was undone (above), and nothing else was changed." \
            "the run that undid a cut-off nginx change, then lost MariaDB"
        ! said "Nothing was changed." \
            || problem "the run that undid a cut-off nginx change, then lost MariaDB, said that nothing was changed" "$(excerpt)"
        same_state "the cut-off change wasn't undone byte for byte before MariaDB was lost" "$nginx_before" "$(nginx_files)"
    else
        problem "the test couldn't set the stand-in mariadb aside"
    fi
    verdict
}

# ---- nginx's reload, pipelog.conf, and what MinerBot's database holds: 90 to 99

case_90() {
    begin 90 "a reload cPanel says worked while nginx kept its old workers, as when nginx's master can't apply the new files: a proxy-key rotation puts nginx's files back and keeps dashboard.env, so nginx and the dashboard still share the old key, and keeps the marker while nginx takes no reload of the old files either; the next run, with a reload nginx takes, undoes it and rotates; without nginx's master to ask, the reload's exit code is all there is, and setup says so"
    local env_before nginx_before key running
    loaded_key() { sed -n 's/.*"\([0-9a-f]\{64\}\)".*/\1/p' /fake/nginx/loaded-proxy-key.conf 2> /dev/null; }
    env_key() { sed -n 's/^Dashboard__ProxyKey=//p' "$home/dashboard.env"; }

    restore deployed
    nginx_workers "nginx: worker process"
    env_before=$(sha256sum < "$home/dashboard.env")
    nginx_before=$(fingerprint /etc/nginx)
    in_home not-taken 120 env FAKE_RELOAD=not-taken ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 1 "the rotation nginx didn't take"
    expect_said "cPanel's reload exited 0, but nginx kept its old worker processes for 2 seconds, so it didn't load the files on disk" \
        "the rotation nginx didn't take"
    expect_said "nginx didn't take cPanel's reload (above), so it still runs the old files, and they're back." "the rotation nginx didn't take"
    expect_said "WARNING: nginx didn't take the reload of the old files either" "the rotation nginx didn't take"
    expect_said "the dashboard runs again on its old settings." "the rotation nginx didn't take"
    ! said "nginx reloaded with the new files" || problem "the rotation nginx didn't take was called reloaded"
    [ "$(sha256sum < "$home/dashboard.env")" = "$env_before" ] || problem "dashboard.env changed, though nginx still sends the old key"
    [ "$(env_key)" = "$key1" ] && [ "$(loaded_key)" = "$key1" ] \
        || problem "nginx and dashboard.env don't share the old key: nginx sends $(loaded_key | cut -c1-8)..., dashboard.env holds $(env_key | cut -c1-8)..."
    same_state "nginx's files aren't back as they were" "$nginx_before" "$(fingerprint /etc/nginx)"
    [ "$(run_calls | grep -c '^restartsrv_nginx ')" = 2 ] || problem "cPanel's reload didn't run exactly twice: for the new files, then for the old"
    run_calls | args_of | grep -qE '^pgrep \[timeout -k 10 10\] -P [0-9]+ -f \^nginx: worker process$' \
        || problem "nginx's workers weren't listed under a time limit"
    [ -e "$home/state/nginx-change" ] || problem "the marker is gone, though nginx took no reload of the old files either"
    read -r _ running _ < /fake/docker/ctr/minerbot-dashboard
    [ "$running" = true ] || problem "the dashboard isn't running again"

    # The next run, with a reload nginx takes: the change left behind is undone first, then the key rotates
    in_home taken 120 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 0 "the next rotation"
    expect_said "An nginx change was cut off earlier. Its files are back as they were before it, and nginx reloaded." "the next rotation"
    key=$(env_key)
    [ "$key" != "$key1" ] && [ "$(loaded_key)" = "$key" ] \
        && [ "$(cat /etc/nginx/minerbot/proxy-key.conf)" = "proxy_set_header X-MinerBot-Proxy-Key \"$key\";" ] \
        || problem "after the next rotation, nginx, its file and dashboard.env don't share the new key"
    [ ! -e "$home/state/nginx-change" ] || problem "the marker is still there after the next rotation"

    # Without nginx's master to ask: no PID file
    restore deployed
    in_home no-pid 120 ./setup-dashboard.sh --no-questions --yes --rotate-proxy-key
    expect_exit 0 "the rotation without nginx's PID file"
    expect_said "Note: nginx's worker processes couldn't be listed from /var/run/nginx.pid" "the rotation without nginx's PID file"
    [ "$(loaded_key)" = "$(env_key)" ] || problem "without nginx's PID file, nginx and dashboard.env don't share the key"
    verdict
}

case_91() {
    begin 91 "pipelog.conf follows WHM's piped logging: setup installs it, \"pipelog off;\" while cPanel loads the module and empty while it doesn't, and cPanel's hook, 755, which writes it the same way each time cPanel rebuilds nginx's files; turning piped logging off, then on, leaves nginx -t passing each time; the dashboard's file never names pipelog itself; the hook does nothing once /etc/nginx/minerbot is gone"
    local hook=/etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog module=/etc/nginx/conf.d/modules/ngx_http_pipelog_module.conf
    local pipelog=/etc/nginx/minerbot/pipelog.conf answer
    restore deployed
    # A server set up before the hook: no pipelog.conf and no hook, and a change to nginx's file brings both
    rm -f "$hook" "$pipelog"
    echo "# A change, so nginx's files differ (made up)" >> "$home/minerbot-dashboard.conf"
    in_home installs 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup with a changed nginx file"
    [ "$(cat "$pipelog" 2> /dev/null)" = "pipelog off;" ] && [ "$(stat -c %a "$pipelog" 2> /dev/null)" = 600 ] \
        || problem "pipelog.conf isn't \"pipelog off;\", 600, while the module loads" "$(cat "$pipelog" 2>&1)"
    [ "$(stat -c '%a %U' "$hook" 2> /dev/null)" = "755 root" ] || problem "cPanel's hook isn't there, 755 and root's" "$(ls -l "$hook" 2>&1)"
    ! grep -qE '^[[:space:]]*pipelog[[:space:]]' /etc/nginx/conf.d/minerbot-dashboard.conf \
        || problem "the dashboard's nginx file names pipelog itself, which nginx refuses without the module"
    [ "$(grep -c '^[[:space:]]*include /etc/nginx/minerbot/pipelog\.con\[f\];$' /etc/nginx/conf.d/minerbot-dashboard.conf)" = 2 ] \
        || problem "the dashboard's nginx file doesn't include pipelog.conf in both its blocks"

    # WHM's piped logging goes off: cPanel deletes the module's line, runs the hook, then tests and reloads nginx
    rm -f "$module"
    answer=$(timeout -k 5 30 "$hook" 2>&1) || problem "the hook failed with piped logging off" "$answer"
    [ -f "$pipelog" ] && [ ! -s "$pipelog" ] || problem "the hook didn't empty pipelog.conf with piped logging off" "$(cat "$pipelog" 2>&1)"
    answer=$(timeout -k 5 30 nginx -t -q 2>&1) || problem "nginx -t fails with piped logging off" "$answer"
    # A setup run meanwhile writes it the same way
    echo "# Another change (made up)" >> "$home/minerbot-dashboard.conf"
    in_home while-off 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup while piped logging is off"
    [ -f "$pipelog" ] && [ ! -s "$pipelog" ] || problem "setup wrote a pipelog line while the module isn't loaded" "$(cat "$pipelog" 2>&1)"

    # On again: the module's line is back, and so is "pipelog off;"
    echo "load_module modules/ngx_http_pipelog_module.so;" > "$module"
    chmod 644 "$module"
    answer=$(timeout -k 5 30 "$hook" 2>&1) || problem "the hook failed with piped logging on" "$answer"
    [ "$(cat "$pipelog" 2> /dev/null)" = "pipelog off;" ] || problem "the hook didn't write \"pipelog off;\" with piped logging on again"
    answer=$(timeout -k 5 30 nginx -t -q 2>&1) || problem "nginx -t fails with piped logging on again" "$answer"
    [ -z "$(find /etc/nginx/minerbot -name '.minerbot-*' 2> /dev/null)" ] || problem "the hook or setup left a temporary file"

    # Once /etc/nginx/minerbot is gone, the hook does nothing and makes nothing
    rm -f /etc/nginx/conf.d/minerbot-dashboard.conf
    rm -rf /etc/nginx/minerbot
    answer=$(timeout -k 5 30 "$hook" 2>&1) || problem "the hook failed without /etc/nginx/minerbot" "$answer"
    [ ! -e /etc/nginx/minerbot ] || problem "the hook made /etc/nginx/minerbot again"
    verdict
}

case_92() {
    begin 92 "a trigger, event or routine in MinerBot's database, which no migration makes, stops the rights before they touch the dashboard's login: setup names it and changes nothing, so does apply_rights as deploy.sh runs it, and a failed look stops them too; once it's gone, the rights apply again"
    local before
    restore deployed
    before=$(home_state)
    in_home setup-trigger 120 env FAKE_DB_EXTRAS='trigger mbtest_stamp' ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "setup with a trigger in MinerBot's database"
    expect_said "MinerBot's database, minerbot_db, holds what no migration makes: trigger mbtest_stamp." "setup with a trigger"
    ! run_calls | grep -qE '\{stdin: (rehearsal|apply)\}' || problem "setup rehearsed or applied the rights despite the trigger"
    same_state "setup with a trigger changed something" "$before" "$(home_state)"
    in_home deploy-extras 60 env FAKE_DB_EXTRAS=$'event mbtest_sweep\nprocedure mbtest_rewrite' \
        bash -c 'source ./common.sh && load_server_conf && apply_rights'
    [ "$status" != 0 ] || problem "apply_rights passed with an event and a procedure in MinerBot's database"
    expect_said "holds what no migration makes: event mbtest_sweep, procedure mbtest_rewrite." "apply_rights with an event and a procedure"
    ! run_calls | grep -qE '\{stdin: (rehearsal|apply)\}' || problem "apply_rights rehearsed or applied despite the event"
    in_home extras-fail 120 env FAKE_DB_EXTRAS=fail ./setup-dashboard.sh --no-questions --yes
    expect_exit 1 "setup when the look at MinerBot's database fails"
    expect_said "Couldn't list the triggers, events and routines in MinerBot's database (above)." "setup when the look fails"
    same_state "setup with a failed look changed something" "$before" "$(home_state)"
    in_home clean 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "setup once the trigger is gone"
    run_calls | grep -qF '{stdin: apply}' || problem "the rights weren't applied once the trigger was gone"
    verdict
}

case_17() {
    begin 17 "backup.sh: a failing dump, or one without its last line, leaves no file and one line on stderr; a working one writes a 600 dump and prunes to 14 days, the newest 3 kept; pre-deploy takes only the backup lock; a run killed from outside is mailed by the cron line and reported by the first nightly check after it, as the cron lines' times make it, though not while its line is younger than 2 hours"
    local dumps_before line cmd stamp expected='' backup_at check_at deploy_wait backup_wait age
    restore deployed
    dumps_before=$(fingerprint /srv/backups/minerbot -name '*.sql.gz')
    backup_run fail 60 env FAKE_DUMP=fail ./backup.sh manual
    expect_exit 1 "the failing dump"
    [ "$(wc -l < "$out/fail.err")" = 1 ] || problem "the failing dump didn't print exactly one line on stderr" "$(cat "$out/fail.err")"
    [ ! -s "$out/fail.out" ] || problem "the failing dump printed on stdout"
    grep -q ' started manual$' /srv/backups/minerbot/backup.log && tail -n 1 /srv/backups/minerbot/backup.log | grep -q ' failed dumping$' \
        || problem "backup.log hasn't a started line, then a failed line" "$(tail -n 4 /srv/backups/minerbot/backup.log)"
    ! find /srv/backups/minerbot \( -name '*-manual.sql.gz' -o -name '*.partial' \) | grep -q . || problem "the failing dump left a file"
    same_state "the failing dump touched the older dumps" "$dumps_before" "$(fingerprint /srv/backups/minerbot -name '*.sql.gz')"

    # A dump that ends early but exits 0: the check after it stops the run, and nothing is kept
    restore deployed
    backup_run truncated 60 env FAKE_DUMP=truncated ./backup.sh manual
    expect_exit 1 "the dump without its last line"
    expect_said "while checking the dump: it doesn't end with -- Dump completed" "the dump without its last line"
    [ "$(wc -l < "$out/truncated.err")" = 1 ] && [ ! -s "$out/truncated.out" ] \
        || problem "the dump without its last line didn't print exactly one line, on stderr" "$(cat "$out/truncated.out" "$out/truncated.err")"
    tail -n 1 /srv/backups/minerbot/backup.log | grep -q ' failed checking the dump$' \
        || problem "backup.log's last line isn't the failed check" "$(tail -n 2 /srv/backups/minerbot/backup.log)"
    ! find /srv/backups/minerbot \( -name '*-manual.sql.gz' -o -name '*.partial' \) | grep -q . \
        || problem "the dump without its last line left a file"

    # Only dumps older than 14 days before this one: the newest 3 stay all the same, the others go
    restore deployed
    rm -f /srv/backups/minerbot/*.sql.gz
    for stamp in 20 21 22 23 24; do
        a_dump "/srv/backups/minerbot/minerbot-202610${stamp}T031700Z-nightly.sql.gz"
        touch -d "-$stamp days" "/srv/backups/minerbot/minerbot-202610${stamp}T031700Z-nightly.sql.gz"
    done
    echo partial > /srv/backups/minerbot/minerbot-20261103T031700Z-nightly.sql.gz.partial
    touch -d '-2 days' /srv/backups/minerbot/minerbot-20261103T031700Z-nightly.sql.gz.partial
    backup_run works 60 ./backup.sh manual
    expect_exit 0 "the working dump"
    [ ! -s "$out/works.out" ] && [ ! -s "$out/works.err" ] || problem "the working dump printed something" "$(cat "$out/works.out" "$out/works.err")"
    find /srv/backups/minerbot -name '*-manual.sql.gz' -perm 600 | grep -q . || problem "no 600 manual dump"
    [ "$(find /srv/backups/minerbot -name 'minerbot-*.sql.gz' | wc -l)" = 3 ] \
        && [ -f /srv/backups/minerbot/minerbot-20261020T031700Z-nightly.sql.gz ] \
        && [ -f /srv/backups/minerbot/minerbot-20261021T031700Z-nightly.sql.gz ] \
        || problem "the prune didn't keep exactly the newest 3 dumps (the new one, 20 and 21 days old)" "$(ls /srv/backups/minerbot)"
    [ ! -e /srv/backups/minerbot/minerbot-20261103T031700Z-nightly.sql.gz.partial ] || problem "a .partial older than a day was kept"
    tail -n 1 /srv/backups/minerbot/backup.log | grep -qE ' ok minerbot-[0-9]{8}T[0-9]{6}Z-manual\.sql\.gz \S+$' \
        || problem "backup.log's last line isn't an ok line" "$(tail -n 2 /srv/backups/minerbot/backup.log)"

    restore deployed
    hold_lock "$home/state/lock"
    backup_run pre-deploy 20 ./backup.sh pre-deploy
    expect_exit 0 "pre-deploy while the deploy lock is held"
    let_go
    hold_lock "$home/state/backup.lock"
    backup_run waits 3 ./backup.sh pre-deploy
    [ "$status" = 124 ] || problem "pre-deploy didn't wait for the backup lock (exit code $status, not the time limit's 124)"
    let_go

    # The age a cut-off nightly run's started line has at least when the nightly check runs, from the server's own
    # schedule: from the backup's cron line plus its two lock waits, the latest it writes that line, to the check's
    # cron line. The checks below backdate the line by that much, so they hold only if the check runs late enough.
    restore deployed
    backup_at=$(awk '!/^#/ && /backup\.sh nightly/ { print $2 * 60 + $1 }' /etc/cron.d/minerbot)
    check_at=$(awk '!/^#/ && /status\.sh --check/ { print $2 * 60 + $1 }' /etc/cron.d/minerbot)
    # shellcheck disable=SC2016 # the $ is text that sed matches in backup.sh
    deploy_wait=$(sed -n 's/.*take_lock "backup \$reason" \([0-9][0-9]*\) .*/\1/p' /repo/deploy/backup.sh)
    backup_wait=$(sed -n 's/.*flock -w \([0-9][0-9]*\) 8 .*/\1/p' /repo/deploy/backup.sh)
    if [[ $backup_at =~ ^[0-9]+$ && $check_at =~ ^[0-9]+$ && $deploy_wait =~ ^[0-9]+$ && $backup_wait =~ ^[0-9]+$ ]]; then
        age=$((((check_at - backup_at + 1440) % 1440) * 60 - deploy_wait - backup_wait))
    else
        problem "couldn't read the two cron lines' times or backup.sh's two lock waits" "$backup_at $check_at $deploy_wait $backup_wait"
        age=0
    fi

    # The cron line, its limit cut to 3 seconds, while the dump hangs. bash 5.1, the server's, runs backup.sh's EXIT
    # trap when timeout ends it there, so backup.log may say failed; either way cron mails the echo, and the check reports it.
    line=$(grep 'backup.sh nightly' /etc/cron.d/minerbot)
    [[ $line == *" root timeout -k 60 3600 /opt/minerbot/backup.sh nightly || echo "* ]] || problem "the cron line isn't the designed one" "$line"
    cmd=${line#* root }
    cmd=${cmd/timeout -k 60 3600/timeout -k 2 3}
    backup_run killed 30 env FAKE_DUMP=hang /bin/bash -c "$cmd"
    said "MinerBot's nightly backup failed or was stopped at its time limit." "$out/killed.out" \
        || problem "the cron line's echo didn't run" "$(cat "$out/killed.out" "$out/killed.err")"
    settle
    stamp=$(date -u -d "-$age seconds" +%Y-%m-%dT%H:%M:%SZ)
    sed -i "\$s/^[^ ]* started nightly\$/$stamp started nightly/" /srv/backups/minerbot/backup.log
    case $(tail -n 1 /srv/backups/minerbot/backup.log) in
        *" started nightly") expected="A backup started at $stamp and never finished" ;;
        *" failed dumping") expected="The last backup failed" ;;
        *) problem "backup.log's last line is neither the started line nor a failed line" "$(tail -n 2 /srv/backups/minerbot/backup.log)" ;;
    esac
    in_home check 60 ./status.sh --check
    expect_said "$expected" "the nightly check after the run stopped at the cron line's limit"

    # Killed outright, as a restart would: no trap can run, so the started line stays alone until the check reports it,
    # which it does at the first nightly check after the run, but not while the line is younger than 2 hours
    restore deployed
    # timeout -s KILL ends its own process group, itself included, so it runs under a shell of its own
    backup_run sigkill 30 env FAKE_DUMP=hang bash -c 'timeout -s KILL 3 ./backup.sh nightly'
    [ "$status" = 137 ] || problem "the killed backup ended with exit code $status, not SIGKILL's 137"
    tail -n 1 /srv/backups/minerbot/backup.log | grep -q ' started nightly$' \
        || problem "backup.log's last line isn't the started line" "$(tail -n 2 /srv/backups/minerbot/backup.log)"
    settle
    stamp=$(date -u -d '-110 minutes' +%Y-%m-%dT%H:%M:%SZ)
    sed -i "\$s/^[^ ]* started nightly\$/$stamp started nightly/" /srv/backups/minerbot/backup.log
    in_home check-early 60 ./status.sh --check
    ! said "never finished" || problem "the nightly check reported a started line 1 hour 50 minutes old" "$(excerpt)"
    stamp=$(date -u -d "-$age seconds" +%Y-%m-%dT%H:%M:%SZ)
    sed -i "\$s/^[^ ]* started nightly\$/$stamp started nightly/" /srv/backups/minerbot/backup.log
    in_home check-sigkill 60 ./status.sh --check
    expect_said "A backup started at $stamp and never finished" "the first nightly check after the killed backup"
    verdict
}

case_18() {
    begin 18 "status.sh: --check names each seeded problem on its own line with exit 1, a socket of another owner, another proxy key in nginx's file than the dashboard's and a public home page answering 404 among them, and is silent with exit 0 when all is well, never reloading nginx; --nginx-look never prints the proxy key or a root-only file; the plain look shows the proxy key and the public home page"
    local f other_key
    other_key=$(printf 'mbtest proxy key other' | sha256sum | cut -c1-64)
    restore deployed
    socket_folder
    in_home fine 60 ./status.sh --check
    expect_exit 0 "the nightly check when all is well"
    [ ! -s "$last" ] || problem "the nightly check printed something when all is well" "$(excerpt)"

    restore deployed
    socket_folder
    touch -d '-40 hours' /srv/backups/minerbot/*.sql.gz
    install -m 600 /work/certs/short-cert.pem /etc/nginx/minerbot/origin-cert.pem
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-bot
    sed -i 's/ healthy / unhealthy /' /fake/docker/ctr/minerbot-dashboard
    # Root's own socket where the dashboard's should be, which only root or the dashboard could have made
    a_socket /run/minerbot-dashboard/dashboard.sock 0 0 600 || problem "the test couldn't make a socket"
    : > /etc/nginx/conf.d/includes-optional/cloudflare.conf
    echo "$home/state/nginx-backup/20261106T000000Z" > "$home/state/nginx-change"
    # nginx sends another key than the dashboard runs with, so the dashboard answers every visitor with 404
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$other_key" > /etc/nginx/minerbot/proxy-key.conf
    in_home problems 60 env FAKE_NGINX_T=fail FAKE_DF=/srv=1048576 FAKE_PUBLIC_ANSWER=404 ./status.sh --check
    expect_exit 1 "the nightly check with problems"
    [ "$(wc -l < "$last")" = 11 ] || problem "the nightly check didn't print exactly 11 lines for the 11 problems" "$(cat "$last")"
    for f in "hours old." "within 30 days" "nginx -t fails, twice a minute apart" "The bot isn't running." "The dashboard is unhealthy." \
        "The dashboard's socket isn't as nginx needs it:" "is empty, so nginx refuses every visitor" "An nginx change was cut off." \
        "Less than 5 GiB is free on /srv" "proxy-key.conf doesn't hold the dashboard's proxy key" \
        "The public home page, https://$host_name/, answered 404 twice, a minute apart"; do
        expect_said "$f" "the nightly check with problems"
    done
    ! said "$other_key" && ! said "$key1" || problem "the nightly check printed a proxy key"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "status.sh reloaded nginx"

    restore deployed
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$key1" >> /etc/nginx/conf.d/ea-nginx.conf
    in_home look 60 ./status.sh --nginx-look
    expect_exit 0 "the nginx look"
    for f in "$key1" "$cpanel_secret" "$private_secret"; do
        ! said "$f" || problem "the nginx look printed a secret"
    done
    expect_said "/etc/nginx/conf.d/ea-nginx.conf: client_max_body_size 128m;" "the nginx look"
    expect_said "user nobody;" "the nginx look"
    expect_said "--user=nobody" "the nginx look"
    ! run_calls | grep -q '^restartsrv_nginx ' || problem "the nginx look reloaded nginx"

    in_home show 60 ./status.sh
    expect_exit 0 "the plain look"
    expect_said "Running now: nothing" "the plain look"
    expect_said "Proxy key: nginx's proxy-key.conf holds the dashboard's key" "the plain look"
    expect_said "Public home page: 200 (https://$host_name/)" "the plain look"
    verdict
}

case_19() {
    begin 19 "across every case: no stand-in secret in any stand-in's arguments, any output or log, or history.log; only the stand-in mariadb's stdin holds them"
    local hits
    sort -u /fake/secrets.txt | awk 'length($0) >= 8' > /tmp/mbtest-secrets
    grep -qF -f /tmp/mbtest-secrets /fake/stdin.log || problem "the secrets list found nothing in mariadb's stdin, so the search itself doesn't work"
    hits=$(grep -rlF -f /tmp/mbtest-secrets /out /evidence "$calls" 2> /dev/null | grep -vxF /out/case13-dashboard.env)
    if [ -n "$hits" ]; then
        problem "a stand-in secret is in these outputs, logs or calls (the files are named, not the secrets)" "$hits"
    fi
    verdict
}

# Every call of a command that must go through run_for, where it stands at a command position: a line's start; after |,
# ;, &, (, {, !, $( or a backtick; after a case item's ")"; after then, do, else, if, elif, while, until, exec, time,
# command, env, nohup or xargs; or after time, env or xargs with options, each with or without one argument of its own,
# as in time -p, xargs -n 1 or env -u HOME. Options after exec and command are left out: in run_for 30 docker exec -i
# <container> mariadb, the limit covers mariadb, and command -v docker only looks docker up. Assignments before the
# command, as in LC_ALL=C mariadb, and a path, as in /usr/sbin/sendmail, don't hide it, while a quoted message never
# passes for an assignment. bash -c counts as a listed command, since that's how the scripts put a pipeline under one
# limit. Comment lines and run_for itself are left out. Each line is matched as it is, so ^ is the line's own start. It
# stays a heuristic: a command named through a variable, such as "$cmd", one after a prefix this list doesn't have, such
# as nice or sudo, one after an option argument with a space in it, and one on a continuation line are missed. Prints
# file:line: text.
unlimited_calls() {   # unlimited_calls <file>...
    awk -v words='bash -c|docker|mariadb|mariadb-dump|curl|git|openssl|nginx|ss|sendmail|systemctl|systemd-run|systemd-tmpfiles|restartsrv_nginx|pgrep|ps' '
        BEGIN {
            q = "\047"
            assign = "([A-Za-z_][A-Za-z0-9_]*=(\"[^\"]*\"|" q "[^" q "]*" q "|[^[:space:]\"" q "()`]*)[[:space:]]+)*"
            pos = "(^|[|;&({!`]|^[[:space:]]*[^[:space:]()]+[)]|(^|[^A-Za-z0-9_])(then|do|else|if|elif|while|until|exec|time|command|env|nohup|xargs)[[:space:]])"
            opts = "(^|[^A-Za-z0-9_])(time|env|xargs)([[:space:]]+-[^[:space:]]+([[:space:]]+[^-[:space:]][^[:space:]]*)?)+[[:space:]]"
            re = "(" pos "|" opts ")[[:space:]]*" assign "([^[:space:]]*/)?(" words ")([[:space:]]|$)"
        }
        /^run_for\(\) \{/ { skip = 1 }
        skip && /^\}/ { skip = 0; next }
        skip || /^[[:space:]]*#/ { next }
        $0 ~ re {
            name = FILENAME
            sub(/.*\//, "", name)
            print name ":" FNR ": " $0
        }' "$@"
}

case_20() {
    begin 20 "static rules: set -Eeuo pipefail and umask 077 in every script, no tracing, every listed command and bash -c under run_for (a check that finds each kind of position in a copy of common.sh, after if, elif, until, exec, time, an assignment, a backtick, a case item, or time, env or xargs with options too, and leaves quoted messages alone), every command the scripts run under run_for checked for by bootstrap.sh before the move, every read with -t, every stty behind on_terminal; across every case, no call without a time limit and no password statement without --skip-print-query-on-error; every script and stand-in recorded as 100755"
    local f found needed paths commands
    local -a probes
    for f in /repo/deploy/*.sh; do
        if [ "${f##*/}" != common.sh ]; then
            grep -qx 'set -Eeuo pipefail' "$f" || problem "${f##*/} has no line: set -Eeuo pipefail"
            grep -qx 'umask 077' "$f" || problem "${f##*/} has no line: umask 077"
        fi
        found=$(grep -nE '^[[:space:]]*set[[:space:]]+-[A-Za-z]*x|xtrace' "$f")
        [ -z "$found" ] || problem "${f##*/} turns tracing on" "$found"
    done
    found=$(unlimited_calls /repo/deploy/*.sh)
    [ -z "$found" ] || problem "a call that isn't under run_for" "$found"
    # The check itself, on a copy of common.sh, which holds run_for: each line added at its end must be found, which
    # also proves that the lines after run_for are checked. Then lines that are fine must not be found.
    # shellcheck disable=SC2016 # lines of text for the check, never run
    probes=('docker ps' '    docker ps' '        nginx -t' 'x=1; curl -s https://example.test/' 'if true; then git status; fi'
        '    out=$(openssl rand -hex 4)' '    ! ss -Hltn' '    /scripts/restartsrv_nginx --reload' '    a | sendmail root'
        '    { systemctl status; }' 'if docker ps; then :; fi' 'elif curl -s x; then' 'until git status; do :; done'
        'while nginx -t -q; do :; done' 'exec docker ps' 'time openssl rand -hex 4' 'LC_ALL=C mariadb -e x' 'x=`ss -H`'
        '    bot) docker ps ;;' 'command docker ps' 'env FOO=1 curl -s x' 'nohup docker ps' 'a | xargs docker rm'
        '    last=$(bash -c '\''gzip -t "$1"'\'' check-dump "$f")' 'x="a b" mariadb -e x' '    systemd-tmpfiles --create x'
        'time -p openssl rand -hex 4' 'env -i curl -s x' 'a | xargs -r docker rm' 'a | xargs -n 1 docker rm'
        'env -u HOME curl -s x' 'a | xargs -I {} docker rm {}')
    { cat /repo/deploy/common.sh; printf '%s\n' "${probes[@]}"; } > /tmp/mbtest-calls.sh
    found=$(unlimited_calls /tmp/mbtest-calls.sh)
    [ "$(printf '%s' "$found" | grep -c .)" = "${#probes[@]}" ] \
        || problem "the check for calls outside run_for didn't find exactly the ${#probes[@]} added to a copy of common.sh" "$found"
    # shellcheck disable=SC2016 # lines of text for the check, never run
    printf '%s\n' '    run_for 30 docker ps' '    echo "the docker ps above"' '    local docker_id=1' '    # docker ps' \
        '    git_url=x' '    x=$(f) || x="(unknown: openssl couldn'"'"'t parse it)"' 'X=$v run_for 180 docker ps' \
        '    if command -v docker > /dev/null; then' '    if ! run_for "$limit" mariadb-dump x; then' \
        '    last=$(run_for 300 bash -c '\''gzip -t "$1"'\'' check-dump "$f")' '    out=$(bash -n "$f" 2>&1)' \
        '    time_left=$(time_left)' '    echo "Wait, then try again: git says so"' '    a | xargs -r -n 1 rm -f' \
        '    run_for 30 docker exec -i minerbot-db mariadb -e x' > /tmp/mbtest-calls.sh
    found=$(unlimited_calls /tmp/mbtest-calls.sh)
    [ -z "$found" ] || problem "the check for calls outside run_for flags lines that are fine" "$found"
    # Every command the scripts run under run_for is one bootstrap.sh checks for before the move: in its list needed,
    # or, given by its path, in needed_commands. bash runs the scripts themselves, and a command given through a
    # variable, such as "$home/backup.sh", is one of the scripts, so neither is looked for.
    needed=" $(sed -n '/^needed=(/,/)$/p' /repo/deploy/bootstrap.sh | sed 's/^needed=(//; s/)$//' | tr -s '[:space:]' ' ') "
    paths=" $(sed -n '/^needed_commands() {/,/^}/p' /repo/deploy/bootstrap.sh | tr -s '[:space:];' ' ') "
    commands=$(grep -vhE '^[[:space:]]*#' /repo/deploy/*.sh \
        | grep -oE '(^|[^A-Za-z0-9_])run_for( -k [0-9]+)? ([0-9]+|"\$[A-Za-z_]+") [^[:space:]"][^[:space:]]*' \
        | awk '{ print $NF }' | sort -u)
    printf '%s\n' "$commands" | grep -qx docker \
        || problem "the search for the commands the scripts run under run_for didn't find docker, so it doesn't work" "$commands"
    found=
    while IFS= read -r f; do
        case $f in
            '' | bash) ;;
            /*) [[ $paths == *" $f "* ]] || found+=" $f" ;;
            *) [[ $needed == *" $f "* ]] || found+=" $f" ;;
        esac
    done <<< "$commands"
    [ -z "$found" ] || problem "bootstrap.sh doesn't check for these commands, which the scripts run under run_for:$found"
    found=$(grep -nE '(^|[|;&({!]|(^|[^A-Za-z0-9_])(then|do|else|while|until|if|elif)[[:space:]])[[:space:]]*(IFS=[^[:space:]]*[[:space:]]+)?read[[:space:]]' \
        /repo/deploy/*.sh | grep -vE ':[0-9]+:[[:space:]]*#' | grep -vE 'read[[:space:]]+(-[a-zA-Z]+[[:space:]]+)*-[a-zA-Z]*t')
    [ -z "$found" ] || problem "a read without -t" "$found"
    found=$(grep -nE '(^|[^A-Za-z0-9_-])stty([[:space:]]|$)' /repo/deploy/*.sh | grep -vE ':[0-9]+:[[:space:]]*#' | grep -v 'on_terminal')
    [ -z "$found" ] || problem "an stty that isn't behind on_terminal" "$found"
    found=$(grep -E '^NO-LIMIT ' "$calls" | sort | uniq -c | sort -rn | head -n 10)
    [ -z "$found" ] || problem "calls without a time limit (their parent wasn't timeout)" "$found"
    found=$(grep -E '^NO-SKIP-PRINT ' "$calls" | head -n 5)
    [ -z "$found" ] || problem "a statement with a password without --skip-print-query-on-error" "$found"
    verdict
}

case_21() {
    begin 21 "setup without a terminal: --no-questions prints the summary, changes nothing and asks for --yes, with no stty error; --no-questions --yes runs; --remove-nginx --yes runs"
    local before
    restore deployed
    before=$(home_state)
    in_home no-yes 60 ./setup-dashboard.sh --no-questions
    expect_exit 1 "--no-questions without a terminal"
    expect_said "Ready to:" "--no-questions without a terminal"
    expect_said "Nothing was changed: without a terminal, add --yes to go ahead." "--no-questions without a terminal"
    ! grep -qi 'stty' "$last" || problem "--no-questions without a terminal printed an stty error" "$(excerpt)"
    same_state "--no-questions without a terminal changed something" "$before" "$(home_state)"
    ! run_calls | grep -q '^systemd-run ' || problem "--no-questions without a terminal started its unit"
    in_home yes 120 ./setup-dashboard.sh --no-questions --yes
    expect_exit 0 "--no-questions --yes without a terminal"
    ! grep -qi 'stty' "$last" || problem "--no-questions --yes printed an stty error" "$(excerpt)"
    in_home remove 120 ./setup-dashboard.sh --remove-nginx --yes
    expect_exit 0 "--remove-nginx --yes without a terminal"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] || problem "--remove-nginx --yes left the conf.d file"
    verdict
}

case_22() {
    begin 22 "fill-secrets.sh: Enter keeps bot.env byte for byte; \$ and ' #' are refused three times; a new value is written 600 by rename with every other line in its place; a deploy meanwhile is refused for the lock; no value in the transcript, logs or calls; it ends with the recreate command"
    local sum inode bg n
    restore deployed
    cp -p "$home/bot.env" /tmp/mbtest-bot.env.before
    sum=$(sha256sum < "$home/bot.env")
    inode=$(stat -c %i "$home/bot.env")
    converse enter "env MINERBOT_TEST_READ_LIMIT=30 $home/fill-secrets.sh" \
        "!BOTTOKEN" $'\r' "!WEBHOOKCHANNEL" $'\r' "!STEAMWEB_PUB_KEY" $'\r' "!STEAMUSER_NAME" $'\r'
    expect_exit 0 "fill-secrets with Enter"
    expect_said "Nothing was changed." "fill-secrets with Enter"
    [ "$(sha256sum < "$home/bot.env")" = "$sum" ] && [ "$(stat -c %i "$home/bot.env")" = "$inode" ] || problem "Enter changed bot.env"

    # shellcheck disable=SC2016 # the $ a value must not have
    converse refused "env MINERBOT_TEST_READ_LIMIT=30 $home/fill-secrets.sh" \
        "!BOTTOKEN" 'a$b0123456789'$'\r' "!BOTTOKEN" 'a #b0123456789'$'\r' "!BOTTOKEN" 'c$d0123456789'$'\r'
    expect_exit 1 "fill-secrets with bad values"
    [ "$(grep -c "That value can't go into bot.env" "$last")" = 3 ] || problem "the bad values weren't refused three times" "$(excerpt)"
    expect_said "Three answers in a row weren't right. Nothing was changed." "fill-secrets with bad values"
    [ "$(sha256sum < "$home/bot.env")" = "$sum" ] || problem "the bad values changed bot.env"

    # A second name for bot.env's inode: a file replaced by rename leaves it with the old bytes
    rm -f /tmp/mbtest-go "$home/state/.mbtest-bot.env"
    ln "$home/bot.env" "$home/state/.mbtest-bot.env"
    (
        converse new "env MINERBOT_TEST_READ_LIMIT=30 $home/fill-secrets.sh" \
            "!BOTTOKEN" $'\r' "FILE:/tmp/mbtest-go" "222222222222222222"$'\r' "!STEAMWEB_PUB_KEY" $'\r' \
            "!STEAMUSER_NAME" "mbtest_user_two"$'\r'
    ) &
    bg=$!
    for n in $(seq 1 100); do
        if grep -q 'WEBHOOKCHANNEL' "$out/new.txt" 2> /dev/null; then break; fi
        sleep 0.2
    done
    in_home meanwhile 60 ./deploy.sh --only bot
    expect_exit 1 "a deploy while fill-secrets runs"
    expect_said "Another run holds the lock: fill-secrets, started" "a deploy while fill-secrets runs"
    touch /tmp/mbtest-go
    wait "$bg"
    last=$out/new.txt
    status=$(cat "$out/new.status" 2> /dev/null)
    expect_exit 0 "fill-secrets with new values"
    [ "$(stat -c %a "$home/bot.env")" = 600 ] || problem "bot.env isn't 600"
    [ "$(stat -c %i "$home/bot.env")" != "$(stat -c %i "$home/state/.mbtest-bot.env")" ] \
        && cmp -s "$home/state/.mbtest-bot.env" /tmp/mbtest-bot.env.before \
        || problem "bot.env was changed in place, not replaced by rename"
    rm -f "$home/state/.mbtest-bot.env"
    [ "$(sed -n 5p "$home/bot.env")" = "WEBHOOKCHANNEL=222222222222222222" ] && [ "$(sed -n 7p "$home/bot.env")" = "STEAMUSER_NAME=mbtest_user_two" ] \
        || problem "the new values aren't on their lines" "$(cut -d= -f1 "$home/bot.env")"
    [ "$(sed '5d;7d' "$home/bot.env")" = "$(sed '5d;7d' /tmp/mbtest-bot.env.before)" ] || problem "other lines of bot.env moved or changed"
    expect_said "docker compose up -d --no-build --no-deps --force-recreate bot" "fill-secrets with new values"
    ! said "222222222222222222" || problem "a typed value is in the transcript"
    ! said "mbtest_user_two" || problem "a typed value is in the transcript"
    ! grep -rqF -e 222222222222222222 -e mbtest_user_two "$home/logs" "$calls" || problem "a typed value is in logs/ or calls.log"
    ! run_calls | args_of | grep -qE ' compose .* up ' || problem "fill-secrets started a container"
    verdict
}

bootstrap_commands() {   # the move's commands, as deploy/README.md gives them
    cat > /fake/bootstrap-commands.sh << 'EOF'
cd /opt/minerbot
install -d -m 700 state state/tmp logs
timeout 120 git -C src fetch --prune --prune-tags --force --tags origin
c=$(timeout 10 git -C src rev-parse --verify 'refs/remotes/origin/main^{commit}')
d=$(mktemp -d /opt/minerbot/state/tmp/bootstrap.XXXXXX)
timeout 60 git -C src archive "$c" deploy | timeout 60 tar -x -C "$d"
timeout 1100 bash "$d/deploy/bootstrap.sh" "$c" "$d"
EOF
}

case_23() {
    begin 23 "bootstrap.sh: a first run keeps today's files in old/, writes server.conf, reserves 1654, asks Compose about today's file and the new one, and installs; a cut after each step is finished by the same commands with old/ unchanged; done is refused; a dry run that recreates refuses; a taken UID warns; the staging folder goes in every outcome but a SIGKILL"
    local reference bot_before n stage left today_sums
    bootstrap_commands
    restore today
    today_sums=$(cd "$home" && sha256sum compose.yml deploy.sh rollback.sh fill-secrets.sh setup-login.sh move-docker-storage.sh README.txt)
    bot_before=$(cat /fake/docker/ctr/minerbot-bot)
    in_home first 180 bash /fake/bootstrap-commands.sh
    expect_exit 0 "the first bootstrap"
    [ "$(cd "$home/old" 2> /dev/null && sha256sum compose.yml deploy.sh rollback.sh fill-secrets.sh setup-login.sh move-docker-storage.sh README.txt)" = "$today_sums" ] \
        || problem "old/ doesn't hold today's seven files byte for byte"
    reference=$(cd "$home/old" 2> /dev/null && sha256sum ./*)
    grep -qx 'SITE_DB=website_db' "$home/server.conf" && grep -qx 'BOT_DB=minerbot_db' "$home/server.conf" \
        && grep -qx 'BOT_READY_PATTERN=Gateway +Ready' "$home/server.conf" && [ "$(stat -c %a "$home/server.conf")" = 600 ] \
        || problem "server.conf doesn't have the two names from bot.env, mode 600"
    [ "$(getent passwd 1654 | cut -d: -f1)" = minerbot-app ] && [ "$(getent group 1654 | cut -d: -f1)" = minerbot-app ] \
        || problem "UID and GID 1654 aren't minerbot-app's"
    in_order "the dry run: today's file, then the new one" \
        'compose -f /opt/minerbot/old/compose.yml --project-directory /opt/minerbot up --dry-run -d --no-build --no-deps bot$' \
        'compose -f /opt/minerbot/state/tmp/bootstrap\.[^/]+/deploy/compose.yml --project-directory /opt/minerbot up --dry-run -d --no-build --no-deps bot$'
    expect_installed "the first bootstrap"
    [ ! -e "$home/setup-login.sh" ] && [ ! -e "$home/move-docker-storage.sh" ] || problem "the two one-off scripts are still there"
    grep -qE "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z $c1\$" "$home/state/bootstrap.done" 2> /dev/null \
        && [ "$(stat -c %a "$home/state/bootstrap.done")" = 600 ] || problem "state/bootstrap.done isn't written, 600"
    [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] || problem "the staging folder is still there" "$(ls -A "$home/state/tmp")"
    [ "$(cat /fake/docker/ctr/minerbot-bot)" = "$bot_before" ] || problem "the bot's container changed"

    # After the move: refused, and nothing changed
    left=$(home_state)
    in_home after-move 120 bash /fake/bootstrap-commands.sh
    expect_exit 1 "the bootstrap after the move"
    expect_said "The move was done on" "the bootstrap after the move"
    same_state "the refused bootstrap changed something" "$left" "$(home_state)"
    [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] || problem "the refused bootstrap left its staging folder"

    # A cut after each step, then the same commands again
    for n in 1 2 3 4 5 6 7 8; do
        restore today
        bootstrap_cut "$n"
        in_home "again-$n" 180 bash /fake/bootstrap-commands.sh
        expect_exit 0 "the bootstrap after a cut at step $n"
        [ "$(cd "$home/old" 2> /dev/null && sha256sum ./*)" = "$reference" ] || problem "after a cut at step $n, old/ isn't today's files"
        [ -f "$home/state/bootstrap.done" ] || problem "after a cut at step $n, the move didn't finish"
        [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] || problem "after a cut at step $n, the cut run's staging folder is still there"
        [ "$(cat /fake/docker/ctr/minerbot-bot)" = "$bot_before" ] || problem "after a cut at step $n, the bot's container changed"
    done

    # A cut inside step 6, after the new deploy.sh was installed and before deploy-files.commit: old/ stays today's
    restore today
    bootstrap_cut 5
    install -m 700 "$stage/deploy/deploy.sh" "$home/deploy.sh"
    in_home inside-6 180 bash /fake/bootstrap-commands.sh
    expect_exit 0 "the bootstrap after a cut inside the install"
    [ "$(cd "$home/old" 2> /dev/null && sha256sum ./*)" = "$reference" ] || problem "after a cut inside the install, old/ took a new file"

    # Compose would recreate the bot
    restore today
    in_home recreate-new 120 env FAKE_DRY_RUN_NEW=recreate bash /fake/bootstrap-commands.sh
    expect_exit 1 "the dry run that recreates with the new file"
    expect_said "The new compose.yml would recreate the bot" "the dry run that recreates with the new file"
    grep -qE 'Container +minerbot-bot +Recreate' "$last" || problem "the refusal didn't print Compose's lines" "$(excerpt)"
    [ ! -e "$home/state/deploy-files.commit" ] && grep -q "today's deploy.sh" "$home/deploy.sh" || problem "something was installed"
    [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] || problem "the refused bootstrap left its staging folder"
    restore today
    in_home recreate-today 120 env FAKE_DRY_RUN_TODAY=recreate bash /fake/bootstrap-commands.sh
    expect_exit 1 "the dry run that recreates with today's file"
    expect_said "Compose would recreate the bot even with today's compose.yml, so bot.env was edited after the bot started" "today's file"
    [ ! -e "$home/state/deploy-files.commit" ] || problem "something was installed"

    # A bad database name in bot.env
    restore today
    sed -i 's/Database=website_db;/Database=web-site;/' "$home/bot.env"
    in_home bad-name 120 bash /fake/bootstrap-commands.sh
    expect_exit 1 "the bootstrap with a bad name in bot.env"
    expect_said "The database in bot.env's ConnectionStrings__DefaultConnection must be 1 to 64 letters, digits and _." "the bad name"
    [ ! -e "$home/server.conf" ] && [ ! -e "$home/state/deploy-files.commit" ] || problem "the bad name still wrote server.conf or installed"

    # UID 1654 taken by another user
    restore today
    useradd --uid 1654 --no-create-home --shell /sbin/nologin mbtest-other > "$out/useradd.out" 2>&1 \
        || problem "useradd couldn't take UID 1654 for the test" "$(cat "$out/useradd.out")"
    in_home uid-taken 180 bash /fake/bootstrap-commands.sh
    expect_exit 0 "the bootstrap with UID 1654 taken"
    expect_said "Warning: UID 1654 belongs to mbtest-other" "the bootstrap with UID 1654 taken"
    [ -f "$home/state/bootstrap.done" ] || problem "the bootstrap with UID 1654 taken didn't finish"
    verdict
}

nothing_moved() {   # nothing_moved <what>: a refused bootstrap installed nothing, finished nothing and left no staging folder
    [ ! -e "$home/state/deploy-files.commit" ] && [ ! -e "$home/state/bootstrap.done" ] && grep -q "today's deploy.sh" "$home/deploy.sh" \
        || problem "$1: something was installed"
    [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] || problem "$1: the staging folder is still there" "$(ls -A "$home/state/tmp")"
}

case_40() {
    begin 40 "bootstrap.sh refuses with its own message and installs nothing: Compose's dry run failing, hanging or printing lines it doesn't know, none of them called a recreate; a config hash that differs; a database bot.env names that isn't on this server, until it is; /opt/minerbot not 700; a missing command, cPanel's reload script or procps-ng's pgrep and ps; a systemd that doesn't end a unit at its limit, or a systemctl that answers nothing about it, or answers too late"
    local bot_before f
    local -a hidden
    bootstrap_commands
    restore today
    bot_before=$(cat /fake/docker/ctr/minerbot-bot)

    # Compose's dry run fails, as when Docker doesn't answer, then hangs until its limit, then prints a line it doesn't know
    in_home dry-fail 120 env FAKE_DRY_RUN_TODAY=fail bash /fake/bootstrap-commands.sh
    expect_exit 1 "the dry run that fails"
    expect_said "Compose's dry run with today's compose.yml failed with exit code 1" "the dry run that fails"
    expect_said "  Cannot connect to the Docker daemon" "the dry run that fails"
    ! said "would recreate" || problem "the dry run that fails was called a recreate" "$(excerpt)"
    nothing_moved "the dry run that fails"
    restore today
    in_home dry-hang 120 env FAKE_DRY_RUN_NEW=hang bash /fake/bootstrap-commands.sh
    expect_exit 1 "the dry run that hangs"
    expect_said "Compose's dry run with the new compose.yml didn't finish within 60 seconds, so it said nothing about the bot." "the dry run that hangs"
    ! said "would recreate" || problem "the dry run that hangs was called a recreate" "$(excerpt)"
    nothing_moved "the dry run that hangs"
    restore today
    in_home dry-odd 120 env FAKE_DRY_RUN_NEW=odd bash /fake/bootstrap-commands.sh
    expect_exit 1 "the dry run with a line it doesn't know"
    expect_said "Compose's dry run with the new compose.yml printed lines this check doesn't recognise" "the dry run with a line it doesn't know"
    grep -qE 'Container +minerbot-bot +Unchanged' "$last" || problem "the refusal didn't print Compose's lines" "$(excerpt)"
    ! said "would recreate" || problem "the dry run with a line it doesn't know was called a recreate" "$(excerpt)"
    nothing_moved "the dry run with a line it doesn't know"

    # Both dry runs pass, but Compose's config hash for the bot differs between the two files
    restore today
    in_home hash-differs 120 env FAKE_HASH_NEW=0123456789abcdef bash /fake/bootstrap-commands.sh
    expect_exit 1 "the bootstrap with a different config hash"
    expect_said "Compose's config hash for the bot differs between today's compose.yml and the new one." "the different config hash"
    ! said "Compose's config hash for the bot is the same for both files." || problem "the different config hash was called the same"
    in_order "the config hash: today's file, then the new one" \
        'compose -f /opt/minerbot/old/compose.yml --project-directory /opt/minerbot config --hash bot$' \
        'compose -f /opt/minerbot/state/tmp/bootstrap\.[^/]+/deploy/compose.yml --project-directory /opt/minerbot config --hash bot$'
    nothing_moved "the different config hash"
    [ "$(cat /fake/docker/ctr/minerbot-bot)" = "$bot_before" ] || problem "the bot's container changed"

    # A database bot.env names isn't on this server: refused before server.conf is written, so the same commands finish
    # the move once both are there
    restore today
    in_home missing-db 120 env FAKE_DB_SCHEMATA=1 bash /fake/bootstrap-commands.sh
    expect_exit 1 "the bootstrap with a database that isn't on this server"
    expect_said "The two databases bot.env names aren't both on this server. Nothing more was changed." "the missing database"
    [ ! -e "$home/server.conf" ] || problem "the missing database still wrote server.conf"
    nothing_moved "the missing database"
    in_home missing-db-again 180 bash /fake/bootstrap-commands.sh
    expect_exit 0 "the bootstrap once both databases are there"
    grep -qx 'BOT_DB=minerbot_db' "$home/server.conf" 2> /dev/null || problem "the bootstrap once both databases are there didn't write server.conf"

    # /opt/minerbot open to others, a missing command, and a systemd that lets a unit outlive its limit: refused in the
    # foreground, before any unit of the move
    restore today
    chmod 755 "$home"
    in_home not-700 60 bash /fake/bootstrap-commands.sh
    chmod 700 "$home"
    expect_exit 1 "the bootstrap with /opt/minerbot 755"
    expect_said "$home must exist, with mode 700. Nothing was changed." "the bootstrap with /opt/minerbot 755"
    ! run_calls | grep -q '^systemd-run ' || problem "the bootstrap with /opt/minerbot 755 started a unit"
    nothing_moved "the bootstrap with /opt/minerbot 755"
    restore today
    chmod -x /scripts/restartsrv_nginx
    in_home no-command 60 bash /fake/bootstrap-commands.sh
    chmod 755 /scripts/restartsrv_nginx
    expect_exit 1 "the bootstrap without cPanel's reload script"
    expect_said "These commands the scripts use are missing: /scripts/restartsrv_nginx. Install them first. Nothing was changed." \
        "the bootstrap without cPanel's reload script"
    ! run_calls | grep -q '^systemd-run ' || problem "the bootstrap without cPanel's reload script started a unit"
    nothing_moved "the bootstrap without cPanel's reload script"
    # Without procps-ng's pgrep and ps, which setup's proof of a reload and the nightly check's look at nginx's workers
    # use: every copy on the PATH is moved aside for this one run. Taking its x bits away isn't enough, since bash's
    # command -v still names a file that isn't executable when it finds nothing else.
    restore today
    mapfile -t hidden < <(type -ap pgrep ps | while IFS= read -r p; do echo "$(readlink -f "${p%/*}")/${p##*/}"; done | sort -u)
    [ ${#hidden[@]} -ge 2 ] || problem "pgrep and ps aren't both on the PATH to move aside" "${hidden[*]}"
    for f in "${hidden[@]}"; do mv "$f" "$f.mbtest-away"; done
    in_home no-procps 60 bash /fake/bootstrap-commands.sh
    for f in "${hidden[@]}"; do mv "$f.mbtest-away" "$f" || problem "couldn't put $f back, so the cases after this one lack it"; done
    expect_exit 1 "the bootstrap without pgrep and ps"
    expect_said "These commands the scripts use are missing: pgrep ps. Install them first. Nothing was changed." \
        "the bootstrap without pgrep and ps"
    ! run_calls | grep -q '^systemd-run ' || problem "the bootstrap without pgrep and ps started a unit"
    nothing_moved "the bootstrap without pgrep and ps"
    restore today
    in_home self-test 120 env FAKE_UNIT_LIMIT=40 bash /fake/bootstrap-commands.sh
    expect_exit 1 "the bootstrap where systemd lets a unit outlive its limit"
    expect_said "systemd doesn't enforce time limits here, so the detached runs can't be trusted. Nothing was changed." \
        "the bootstrap where systemd lets a unit outlive its limit"
    ! run_calls | grep -q -- '--unit=minerbot-bootstrap ' || problem "the failed self-test still started the move's unit"
    nothing_moved "the bootstrap where systemd lets a unit outlive its limit"
    # A systemctl that answers nothing about the self-test unit, as one that hits its 10 seconds would, proves nothing.
    # The stand-in systemctl gets a wrapper for this one run, and the case puts it back however it ends.
    restore today
    trap 'if [ -e /usr/local/bin/systemctl.mbtest-40 ]; then mv -f /usr/local/bin/systemctl.mbtest-40 /usr/local/bin/systemctl; fi' EXIT
    if mv /usr/local/bin/systemctl /usr/local/bin/systemctl.mbtest-40; then
        cat > /usr/local/bin/systemctl << 'EOF'
#!/usr/bin/env bash
if [ "${1:-} ${2:-}" = "is-active minerbot-selftest" ]; then
    exit 1
fi
exec /usr/local/bin/systemctl.mbtest-40 "$@"
EOF
        chmod 755 /usr/local/bin/systemctl
        in_home self-test-silent 120 bash /fake/bootstrap-commands.sh
        expect_exit 1 "the bootstrap where systemctl answers nothing about the self-test unit"
        expect_said "systemctl didn't answer in time whether the self-test unit still runs, so the detached runs can't be trusted. Nothing was changed." \
            "the bootstrap where systemctl answers nothing about the self-test unit"
        ! run_calls | grep -q -- '--unit=minerbot-bootstrap ' \
            || problem "the self-test that systemctl answered nothing about still started the move's unit"
        nothing_moved "the bootstrap where systemctl answers nothing about the self-test unit"
        # Its first answer comes after run_for's 10 seconds, so it's silent, and the next says the unit is gone: when it
        # ended is unknown, so that's no proof either, and systemd isn't blamed for it
        restore today
        rm -f /run/mbtest-40-slow-once
        cat > /usr/local/bin/systemctl << 'EOF'
#!/usr/bin/env bash
if [ "${1:-} ${2:-}" = "is-active minerbot-selftest" ] && [ ! -e /run/mbtest-40-slow-once ]; then
    : > /run/mbtest-40-slow-once
    sleep 15
fi
exec /usr/local/bin/systemctl.mbtest-40 "$@"
EOF
        in_home self-test-late 120 bash /fake/bootstrap-commands.sh
        mv -f /usr/local/bin/systemctl.mbtest-40 /usr/local/bin/systemctl
        rm -f /run/mbtest-40-slow-once
        expect_exit 1 "the bootstrap where systemctl's first answer about the self-test unit comes too late"
        expect_said "systemctl didn't answer in time whether the self-test unit still runs, so the detached runs can't be trusted. Nothing was changed." \
            "the bootstrap where systemctl's first answer about the self-test unit comes too late"
        ! said "systemd doesn't enforce time limits here" \
            || problem "the bootstrap blamed systemd when only systemctl's first answer came too late"
        ! run_calls | grep -q -- '--unit=minerbot-bootstrap ' \
            || problem "the self-test whose first answer came too late still started the move's unit"
        nothing_moved "the bootstrap where systemctl's first answer about the self-test unit comes too late"
    else
        problem "the test couldn't set the stand-in systemctl aside"
    fi
    verdict
}

case_41() {
    begin 41 "bootstrap.sh after the install and around its unit: after a cut at step 6, an old/ copy that went missing is never made from a new file, and the one-off script without its copy stays; the reserved account can't sign in; the bot's container changing during the move stops it before it's done; a hang-up of the terminal while the unit runs leaves the staging folder to the unit, which finishes the move"
    local stage_dir n bg
    bootstrap_commands

    # A cut at step 6, after the install, then two of old/'s copies go missing
    restore today
    bootstrap_cut 6
    rm -f "$home/old/rollback.sh" "$home/old/setup-login.sh"
    in_home old-missing 180 bash /fake/bootstrap-commands.sh
    expect_exit 0 "the bootstrap after a cut at step 6 with two old/ copies gone"
    expect_said "Warning: old/rollback.sh is missing, and the new deploy files are installed already, so it's never made now" \
        "the bootstrap after a cut at step 6 with two old/ copies gone"
    [ ! -e "$home/old/rollback.sh" ] || problem "old/rollback.sh was made after the install, from the new file"
    [ -e "$home/setup-login.sh" ] || problem "setup-login.sh was removed though its copy in old/ is gone"
    [ ! -e "$home/move-docker-storage.sh" ] || problem "move-docker-storage.sh, whose copy is in old/, is still there"
    [ -f "$home/state/bootstrap.done" ] || problem "the move didn't finish"
    [ "$(getent passwd 1654)" = "minerbot-app:x:1654:1654:MinerBot containers (reserved):/nonexistent:/sbin/nologin" ] \
        && [ "$(getent group 1654)" = "minerbot-app:x:1654:" ] \
        || problem "UID and GID 1654 aren't a system account that can't sign in" "$(getent passwd 1654; getent group 1654)"

    # The bot's container changes during the move: its second look at the bot, at step 8, sees a new start time
    restore today
    in_home bot-changed 180 env FAKE_BOT_RESTARTS_AT=2 bash /fake/bootstrap-commands.sh
    expect_exit 1 "the bootstrap while the bot restarted"
    expect_said "The bot's container changed during the move" "the bootstrap while the bot restarted"
    [ ! -e "$home/state/bootstrap.done" ] || problem "the move was called done though the bot's container changed"

    # The terminal hangs up while the unit runs, a slow dry run keeping it busy: the foreground goes, the unit finishes
    restore today
    ( in_home hup 180 env FAKE_DRY_RUN_DELAY=4 bash /fake/bootstrap-commands.sh ) &
    bg=$!
    for n in $(seq 1 150); do
        if grep -q 'runs on its own now' "$out/hup.out" 2> /dev/null; then break; fi
        sleep 0.2
    done
    stage_dir=$(find "$home/state/tmp" -mindepth 1 -maxdepth 1 -name 'bootstrap.*' | head -n 1)
    pkill -HUP -f '^bash /opt/minerbot/state/tmp/bootstrap\.[^ ]+/deploy/bootstrap\.sh [0-9a-f]{40} ' \
        || problem "the test found no foreground bootstrap.sh to hang up"
    wait "$bg"
    last=$out/hup.out
    expect_said "The bootstrap goes on. Follow it again with: tail -f" "the bootstrap whose terminal hung up"
    [ -n "$stage_dir" ] && [ -d "$stage_dir" ] || problem "the staging folder went with the foreground, while the unit still needed it"
    for n in $(seq 1 150); do
        if [ -f "$home/state/bootstrap.done" ] && [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ]; then break; fi
        sleep 0.2
    done
    [ -f "$home/state/bootstrap.done" ] || problem "the unit didn't finish the move after the hang-up" "$(tail -n 5 "$(last_log bootstrap)")"
    [ -z "$(ls -A "$home/state/tmp" 2> /dev/null)" ] || problem "the unit didn't remove the staging folder" "$(ls -A "$home/state/tmp")"
    verdict
}

case_42() {
    begin 42 "backup.sh, more: nightly and manual wait for the deploy lock; less than 1 GiB free refuses with one line before the dump, and exactly 1 GiB is enough; the dump is exactly the designed mariadb-dump command, with pre-deploy's own limit; a working run makes both folders 700 again, and on a first night makes them 700 with a 600 dump and backup.log; past the newest 3, a dump younger than 14 days stays and an older one goes"
    local dumps_before expected starts stamp
    restore deployed
    starts=$(grep -c ' started ' /srv/backups/minerbot/backup.log)
    hold_lock "$home/state/lock"
    backup_run nightly-waits 3 ./backup.sh nightly
    [ "$status" = 124 ] || problem "nightly didn't wait for the deploy lock (exit code $status, not the time limit's 124)"
    backup_run manual-waits 3 ./backup.sh manual
    [ "$status" = 124 ] || problem "manual didn't wait for the deploy lock (exit code $status, not the time limit's 124)"
    let_go
    [ "$(grep -c ' started ' /srv/backups/minerbot/backup.log)" = "$starts" ] \
        || problem "a dump started while a deploy held the lock" "$(tail -n 4 /srv/backups/minerbot/backup.log)"

    # Less than 1 GiB free where the dumps go: refused before mariadb-dump runs, with one line, no dump kept or removed
    restore deployed
    dumps_before=$(fingerprint /srv/backups/minerbot -name '*.sql.gz')
    backup_run full 60 env FAKE_DF=/srv=1048575 ./backup.sh manual
    expect_exit 1 "the dump with less than 1 GiB free"
    expect_said "while preparing /srv/backups/minerbot: less than 1 GiB is free there" "the dump with less than 1 GiB free"
    [ "$(wc -l < "$out/full.err")" = 1 ] && [ ! -s "$out/full.out" ] \
        || problem "the refused dump didn't print exactly one line, on stderr" "$(cat "$out/full.out" "$out/full.err")"
    ! run_calls | grep -q '^mariadb-dump ' || problem "mariadb-dump ran with less than 1 GiB free"
    tail -n 1 /srv/backups/minerbot/backup.log | grep -q ' failed preparing /srv/backups/minerbot$' \
        || problem "backup.log's last line isn't the failed free-space check" "$(tail -n 2 /srv/backups/minerbot/backup.log)"
    ! find /srv/backups/minerbot \( -name '*-manual.sql.gz' -o -name '*.partial' \) | grep -q . || problem "the refused dump left a file"
    same_state "the refused dump touched the older dumps" "$dumps_before" "$(fingerprint /srv/backups/minerbot -name '*.sql.gz')"
    restore deployed
    backup_run edge 60 env FAKE_DF=/srv=1048576 ./backup.sh manual
    expect_exit 0 "the dump with exactly 1 GiB free"

    # The exact dump command: one consistent snapshot without locks, no session rows, and no triggers, routines or
    # events, each left out on the command line, which wins over root's option files, as do the rows, tables and drops
    restore deployed
    backup_run exact 60 ./backup.sh manual
    expect_exit 0 "the working dump"
    expected="mariadb-dump [timeout -k 10 900] --single-transaction --quick --hex-blob --skip-dump-date --default-character-set=utf8mb4 --skip-triggers --skip-routines --skip-events --skip-no-data --skip-no-create-info --add-drop-table --ignore-table-data=minerbot_db.dashboard_sessions minerbot_db"
    [ "$(run_calls | args_of | grep -E '^mariadb-dump ')" = "$expected" ] \
        || problem "the dump isn't exactly the designed command" "$(run_calls | args_of | grep -E '^mariadb-dump ')"
    restore deployed
    hold_lock "$home/state/lock"
    backup_run exact-pre 30 ./backup.sh pre-deploy
    let_go
    expect_exit 0 "the pre-deploy dump"
    [ "$(run_calls | args_of | grep -E '^mariadb-dump ')" = "${expected/\[timeout -k 10 900\]/[timeout -k 10 300]}" ] \
        || problem "the pre-deploy dump isn't exactly the designed command, with its limit of 300 seconds" "$(run_calls | args_of | grep -E '^mariadb-dump ')"

    # Both folders left open, as a /srv/backups made by hand would be: a working run closes them to root again
    restore deployed
    chmod 755 /srv/backups /srv/backups/minerbot
    backup_run reclose 60 ./backup.sh manual
    expect_exit 0 "the dump with both folders 755"
    [ "$(stat -c %a /srv/backups)" = 700 ] && [ "$(stat -c %a /srv/backups/minerbot)" = 700 ] \
        || problem "the working dump didn't make /srv/backups and /srv/backups/minerbot 700" "$(stat -c '%a %n' /srv/backups /srv/backups/minerbot)"
    # The first night, without /srv/backups at all
    restore deployed
    rm -rf /srv/backups
    backup_run first 60 ./backup.sh manual
    expect_exit 0 "the first dump, without /srv/backups"
    [ "$(stat -c %a /srv/backups 2> /dev/null)" = 700 ] && [ "$(stat -c %a /srv/backups/minerbot 2> /dev/null)" = 700 ] \
        && [ "$(stat -c %a /srv/backups/minerbot/backup.log 2> /dev/null)" = 600 ] \
        && find /srv/backups/minerbot -name '*-manual.sql.gz' -perm 600 | grep -q . \
        || problem "the first dump didn't leave /srv/backups and /srv/backups/minerbot 700, with a 600 dump and backup.log" \
            "$(stat -c '%a %n' /srv/backups /srv/backups/minerbot /srv/backups/minerbot/* 2>&1)"

    # Past the newest 3, a dump younger than 14 days stays and an older one goes. 13.5 and 14.5 days old sit either side
    # of -mtime +13, which counts whole days; each name's day is that dump's age in whole days.
    restore deployed
    rm -f /srv/backups/minerbot/*.sql.gz
    for stamp in 02 03 04 13 14 20; do
        a_dump "/srv/backups/minerbot/minerbot-202610${stamp}T031700Z-nightly.sql.gz"
        touch -d "-$((10#$stamp * 24 + 12)) hours" "/srv/backups/minerbot/minerbot-202610${stamp}T031700Z-nightly.sql.gz"
    done
    backup_run window 60 ./backup.sh manual
    expect_exit 0 "the dump beside younger ones"
    [ "$(find /srv/backups/minerbot -name 'minerbot-*.sql.gz' | wc -l)" = 5 ] \
        && [ -f /srv/backups/minerbot/minerbot-20261004T031700Z-nightly.sql.gz ] \
        && [ -f /srv/backups/minerbot/minerbot-20261013T031700Z-nightly.sql.gz ] \
        && [ ! -e /srv/backups/minerbot/minerbot-20261014T031700Z-nightly.sql.gz ] \
        && [ ! -e /srv/backups/minerbot/minerbot-20261020T031700Z-nightly.sql.gz ] \
        || problem "the prune didn't keep exactly the dumps younger than 14 days (the new one, 2.5, 3.5, 4.5 and 13.5 days old)" \
            "$(ls /srv/backups/minerbot)"
    verdict
}

case_43() {
    begin 43 "status.sh, more: the dashboard's socket, with its folder open to others whether the dashboard runs or not, its folder missing, a file at the socket's place while the dashboard is stopped, the socket missing while the dashboard runs, and nothing to say while the dashboard is stopped and its socket rightly gone; an older dump beside newer ones is fine; 35 hours is fine and 37 isn't; nginx -t failing once, as during a reload, isn't reported; Docker not answering is; --mail-test sends with sendmail -t and says when the mail system refuses; the plain look shows the socket"
    local f
    # The socket's folder open to every account, while the dashboard runs
    restore deployed
    socket_folder
    chmod 2777 /run/minerbot-dashboard
    in_home open-folder 60 ./status.sh --check
    expect_exit 1 "the nightly check with the socket's folder open to others"
    [ "$(wc -l < "$last")" = 1 ] && said "The dashboard's socket isn't as nginx needs it:" && said "/run/minerbot-dashboard" \
        || problem "the nightly check didn't name the open socket folder, and only that" "$(cat "$last")"

    # No folder at all, and the dashboard stopped
    restore deployed
    rm -rf /run/minerbot-dashboard
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-dashboard
    in_home no-folder 60 ./status.sh --check
    expect_exit 1 "the nightly check without the socket's folder"
    [ "$(wc -l < "$last")" = 2 ] && said "The dashboard isn't running." && said "The dashboard's socket isn't as nginx needs it:" \
        || problem "the nightly check didn't name the stopped dashboard and the missing folder, and only those" "$(cat "$last")"

    # The folder open to every account while the dashboard is stopped: any account could make dashboard.sock there
    # before the dashboard starts again, so that's a problem too
    restore deployed
    socket_folder
    chmod 2777 /run/minerbot-dashboard
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-dashboard
    in_home open-stopped 60 ./status.sh --check
    expect_exit 1 "the nightly check with the socket's folder open to others and the dashboard stopped"
    [ "$(wc -l < "$last")" = 2 ] && said "The dashboard isn't running." \
        && said "The dashboard's socket isn't as nginx needs it: its socket's folder /run/minerbot-dashboard is 2777 1654 nobody directory" \
        || problem "the nightly check with the dashboard stopped didn't name the open socket folder too, and only those two" "$(cat "$last")"

    # A file at the socket's place while the dashboard is stopped is a problem too
    restore deployed
    socket_folder
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-dashboard
    install -m 660 -o 1654 -g nobody /dev/null /run/minerbot-dashboard/dashboard.sock
    in_home file-stopped 60 ./status.sh --check
    expect_exit 1 "the nightly check with a file at the socket's place and the dashboard stopped"
    [ "$(wc -l < "$last")" = 2 ] && said "The dashboard isn't running." \
        && said "/run/minerbot-dashboard/dashboard.sock isn't a socket owned by 1654 and the group nobody: it's a regular empty file" \
        || problem "the nightly check with the dashboard stopped didn't name the file at the socket's place too, and only those two" "$(cat "$last")"

    # The folder as it should be and the dashboard stopped: its socket is rightly gone, so only the stop is a problem
    restore deployed
    socket_folder
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-dashboard
    in_home stopped 60 ./status.sh --check
    expect_exit 1 "the nightly check with the dashboard stopped"
    [ "$(wc -l < "$last")" = 1 ] && said "The dashboard isn't running." \
        || problem "the nightly check with the dashboard stopped didn't say only that it isn't running" "$(cat "$last")"

    # The dashboard runs, but its socket isn't there
    restore deployed
    socket_folder
    in_home no-socket 60 env FAKE_NO_SOCKET="dashboard@${c1:0:12}" ./status.sh --check
    expect_exit 1 "the nightly check with the dashboard's socket missing"
    [ "$(wc -l < "$last")" = 1 ] && said "The dashboard's socket isn't as nginx needs it:" \
        || problem "the nightly check didn't name the missing socket, and only that" "$(cat "$last")"

    # An older dump beside the newer ones: the newest is what counts
    restore deployed
    socket_folder
    a_dump /srv/backups/minerbot/minerbot-20261031T031700Z-nightly.sql.gz
    touch -d '-5 days' /srv/backups/minerbot/minerbot-20261031T031700Z-nightly.sql.gz
    in_home older-dump 60 ./status.sh --check
    expect_exit 0 "the nightly check with an older dump beside newer ones"
    [ ! -s "$last" ] || problem "the nightly check with an older dump beside newer ones printed something" "$(excerpt)"
    # The 36-hour rule's edge: 35 hours old is fine, 37 isn't
    touch -d '-35 hours' /srv/backups/minerbot/*.sql.gz
    in_home hours-35 60 ./status.sh --check
    ! said "hours old." || problem "the nightly check reported a newest dump 35 hours old" "$(excerpt)"
    touch -d '-37 hours' /srv/backups/minerbot/*.sql.gz
    in_home hours-37 60 ./status.sh --check
    expect_said "is 37 hours old." "the nightly check with a newest dump 37 hours old"

    # nginx -t fails once, as during a reload, and passes a minute later: nothing to report
    restore deployed
    socket_folder
    in_home nginx-once 60 env FAKE_NGINX_T=fail-once ./status.sh --check
    expect_exit 0 "the nightly check with nginx -t failing once"
    [ ! -s "$last" ] || problem "the nightly check reported nginx -t failing once" "$(excerpt)"
    [ "$(run_calls | args_of | grep -c '^nginx \[timeout -k 10 30\] -t -q$')" = 2 ] \
        || problem "nginx -t didn't run twice" "$(run_calls | args_of | grep '^nginx ')"

    # Docker doesn't answer
    in_home docker-down 60 env FAKE_DOCKER_DOWN=1 ./status.sh --check
    expect_exit 1 "the nightly check with Docker not answering"
    expect_said "Docker didn't answer about the bot: Cannot connect to the Docker daemon" "the nightly check with Docker not answering"

    # The mail test: one message to root through sendmail -t, and a clear failure when the mail system refuses it
    in_home mail 60 ./status.sh --mail-test
    expect_exit 0 "the mail test"
    expect_said "Sent a test message to root." "the mail test"
    [ "$(run_calls | args_of | grep '^sendmail ')" = "sendmail [timeout -k 10 30] -t" ] \
        || problem "the mail test didn't run sendmail -t under timeout 30" "$(run_calls | args_of | grep '^sendmail ')"
    f=$(find /fake/mail -type f 2> /dev/null | head -n 1)
    [ -n "$f" ] && grep -qx 'To: root' "$f" && grep -qx 'Subject: MinerBot mail test' "$f" \
        || problem "the test message isn't to root with its subject" "$(cat "$f" 2> /dev/null)"
    in_home mail-refused 60 env FAKE_SENDMAIL=fail ./status.sh --mail-test
    expect_exit 1 "the mail test the mail system refuses"
    expect_said "The mail system didn't take the test message (above)." "the mail test the mail system refuses"

    # The plain look names the socket
    in_home show 60 ./status.sh
    expect_exit 0 "the plain look"
    expect_said "Dashboard socket: /run/minerbot-dashboard/dashboard.sock, as nginx needs it" "the plain look"
    verdict
}

case_44() {
    begin 44 "fill-secrets.sh, more: another account is refused before anything; a new value and then three refused answers leave bot.env byte for byte, with the same inode, and the typed value nowhere"
    local sum inode
    restore deployed
    # Its own copy of the scripts, since only root can open /opt/minerbot
    install -d -m 755 /tmp/mbtest-fill
    install -m 755 /repo/deploy/fill-secrets.sh /repo/deploy/common.sh /tmp/mbtest-fill/
    in_home not-root 60 runuser -u nobody -- env -C /tmp /tmp/mbtest-fill/fill-secrets.sh
    expect_exit 1 "fill-secrets as another account"
    expect_said "Run this as root. Nothing was changed." "fill-secrets as another account"

    # A new BOTTOKEN, then three answers for WEBHOOKCHANNEL that can't go into bot.env: nothing is written
    restore deployed
    sum=$(sha256sum < "$home/bot.env")
    inode=$(stat -c %i "$home/bot.env")
    # shellcheck disable=SC2016 # the $ a value must not have
    converse half "env MINERBOT_TEST_READ_LIMIT=30 $home/fill-secrets.sh" \
        "!BOTTOKEN" "MbtestNewToken.made-up.9876543210"$'\r' "!WEBHOOKCHANNEL" 'a$b0123456789'$'\r' \
        "!WEBHOOKCHANNEL" 'a #b0123456789'$'\r' "!WEBHOOKCHANNEL" 'c$d0123456789'$'\r'
    expect_exit 1 "fill-secrets with a new value, then three refused ones"
    expect_said "Three answers in a row weren't right. Nothing was changed." "fill-secrets with a new value, then three refused ones"
    [ "$(sha256sum < "$home/bot.env")" = "$sum" ] && [ "$(stat -c %i "$home/bot.env")" = "$inode" ] \
        || problem "bot.env changed before every question had its answer"
    ! said "MbtestNewToken.made-up.9876543210" || problem "the typed value is in the transcript"
    ! grep -rqF MbtestNewToken.made-up.9876543210 "$home/logs" "$calls" || problem "the typed value is in logs/ or calls.log"
    verdict
}

public_calls() {   # how many times the last run asked the public home page through Cloudflare
    run_calls | args_of | grep -c "^curl .* https://$host_name/\$"
}

case_45() {
    begin 45 "status.sh, the public path: --check reports nginx's proxy-key.conf missing, or not holding the dashboard's proxy key, which is its container's (a setup cut off before its recreate leaves dashboard.env ahead of it) or, without a container, dashboard.env's; any of nginx's workers older than that file, also beside a newer one, but not an old one shutting down after a reload, and not while an nginx change is cut off; a public home page that answers 404, 522 or nothing twice, a minute apart, but not 404 once, a Cloudflare challenge, or a stopped dashboard's page; the plain look shows the key and the page; no key printed, nothing changed, nginx never reloaded"
    local other_key other_line before mark f
    other_key=$(printf 'mbtest proxy key other' | sha256sum | cut -c1-64)
    other_line="proxy_set_header X-MinerBot-Proxy-Key \"$other_key\";"
    mark=$(wc -l < "$calls")

    # All is well: nginx's file holds the key the dashboard's container runs with, and the home page answers 200 at once
    restore deployed
    socket_folder
    before=$(home_state)
    in_home fine 60 ./status.sh --check
    expect_exit 0 "the nightly check when all is well"
    [ ! -s "$last" ] || problem "the nightly check printed something when all is well" "$(excerpt)"
    [ "$(public_calls)" = 1 ] || problem "the nightly check didn't ask the public home page exactly once" "$(run_calls | args_of | grep '^curl ')"
    run_calls | args_of | grep -qF 'container inspect -f {{range .Config.Env}}{{println .}}{{end}} minerbot-dashboard' \
        || problem "the nightly check didn't read the environment the dashboard's container runs with" "$(run_calls | args_of | grep '^docker ')"
    same_state "the nightly check changed a file" "$before" "$(home_state)"

    # proxy-key.conf gone: nginx -t still passes, since its include is a pattern, and the dashboard answers 404
    rm -f /etc/nginx/minerbot/proxy-key.conf
    in_home no-file 60 env FAKE_PUBLIC_ANSWER=404 ./status.sh --check
    expect_exit 1 "the nightly check without proxy-key.conf"
    [ "$(wc -l < "$last")" = 2 ] && said "nginx's /etc/nginx/minerbot/proxy-key.conf is missing, so the dashboard answers every visitor with 404." \
        && said "answered 404 twice, a minute apart" \
        || problem "the nightly check didn't name the missing proxy-key.conf and the 404s, and only those" "$(cat "$last")"

    # nginx's file holds another key than the dashboard's
    restore deployed
    socket_folder
    printf '%s\n' "$other_line" > /etc/nginx/minerbot/proxy-key.conf
    in_home other-key 60 ./status.sh --check
    expect_exit 1 "the nightly check with another key in nginx's file"
    [ "$(wc -l < "$last")" = 1 ] && said "proxy-key.conf doesn't hold the dashboard's proxy key, so the dashboard answers every visitor with 404." \
        && said "./setup-dashboard.sh --no-questions --rotate-proxy-key" \
        || problem "the nightly check didn't name the other key in nginx's file, and only that" "$(cat "$last")"

    # A setup cut off after it moved dashboard.env, before it recreated the dashboard: nginx's file and dashboard.env hold
    # the new key, and the container still runs with the old one. The container's is what counts.
    restore deployed
    socket_folder
    mkdir -p /fake/docker/env
    sed -n "s/^Dashboard__ProxyKey=.*/Dashboard__ProxyKey=$other_key/; /^[A-Za-z_][A-Za-z0-9_]*=/p" "$home/dashboard.env" \
        > /fake/docker/env/minerbot-dashboard
    in_home old-container 60 ./status.sh --check
    expect_exit 1 "the nightly check with the dashboard's container on an older key"
    [ "$(wc -l < "$last")" = 1 ] && said "proxy-key.conf doesn't hold the dashboard's proxy key" \
        || problem "the nightly check didn't see that the dashboard's container runs another key than nginx sends" "$(cat "$last")"
    # nginx's file and the container agree, so visitors get through, whatever dashboard.env says
    printf '%s\n' "$other_line" > /etc/nginx/minerbot/proxy-key.conf
    in_home container-key 60 ./status.sh --check
    expect_exit 0 "the nightly check with nginx's file and the container on the same key"
    [ ! -s "$last" ] || problem "the nightly check reported a key nginx and the dashboard's container share" "$(excerpt)"

    # Without a container, dashboard.env's key is the one that counts, and the public home page isn't asked
    restore deployed
    socket_folder
    rm -f /fake/docker/ctr/minerbot-dashboard
    in_home no-container 60 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "The dashboard isn't running." \
        || problem "the nightly check without the dashboard's container didn't say only that it isn't running" "$(cat "$last")"
    [ "$(public_calls)" = 0 ] || problem "the nightly check asked the public home page while the dashboard has no container"
    printf '%s\n' "$other_line" > /etc/nginx/minerbot/proxy-key.conf
    in_home no-container-other 60 ./status.sh --check
    [ "$(wc -l < "$last")" = 2 ] && said "The dashboard isn't running." && said "proxy-key.conf doesn't hold the dashboard's proxy key" \
        || problem "the nightly check without a container didn't compare nginx's file with dashboard.env" "$(cat "$last")"

    # nginx's workers started after proxy-key.conf was written, so nginx has loaded it
    restore deployed
    socket_folder
    nginx_workers "nginx: worker process"
    in_home loaded 60 ./status.sh --check
    expect_exit 0 "the nightly check with nginx's workers newer than proxy-key.conf"
    [ ! -s "$last" ] || problem "the nightly check reported workers newer than proxy-key.conf" "$(excerpt)"
    # The same line written again, by a copy and a rename as a restore writes it: no worker started since
    sleep 3
    cp -p /etc/nginx/minerbot/proxy-key.conf /etc/nginx/minerbot/.mbtest-proxy-key
    mv -f /etc/nginx/minerbot/.mbtest-proxy-key /etc/nginx/minerbot/proxy-key.conf
    in_home not-loaded 60 ./status.sh --check
    expect_exit 1 "the nightly check with nginx's workers older than proxy-key.conf"
    [ "$(wc -l < "$last")" = 1 ] && said "nginx's workers started before /etc/nginx/minerbot/proxy-key.conf last changed" \
        && said "Run /scripts/restartsrv_nginx --reload, which loads it." \
        || problem "the nightly check didn't name nginx's workers older than proxy-key.conf, and only that" "$(cat "$last")"
    # While an nginx change is cut off, setup's undo is the way, not a reload
    echo "$home/state/nginx-backup/20261106T000000Z" > "$home/state/nginx-change"
    in_home cut-off 60 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "An nginx change was cut off." \
        || problem "the nightly check during a cut-off nginx change didn't say only that" "$(cat "$last")"
    rm -f "$home/state/nginx-change"
    # A worker started since, as the master starts one in place of another that crashed, beside the old one still taking
    # requests: no reload came, so nginx still hasn't loaded the file
    nginx_reload
    in_home one-new 60 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "nginx's workers started before /etc/nginx/minerbot/proxy-key.conf last changed" \
        || problem "the nightly check took one new worker for a reload, beside an old one still taking requests" "$(cat "$last")"
    # A file that doesn't hold the dashboard's key isn't worth a reload: only the key is reported
    printf '%s\n' "$other_line" > /etc/nginx/minerbot/proxy-key.conf
    in_home other-not-loaded 60 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "proxy-key.conf doesn't hold the dashboard's proxy key" \
        || problem "the nightly check advised a reload of a proxy-key.conf without the dashboard's key" "$(cat "$last")"
    # After a reload: a new worker, and the one before still finishing its requests, as nginx retitles it. The file holds
    # the dashboard's key again, written as setup writes it.
    nginx_workers "nginx: worker process is shutting down"
    sleep 3
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$key1" > /etc/nginx/minerbot/.mbtest-proxy-key
    mv -f /etc/nginx/minerbot/.mbtest-proxy-key /etc/nginx/minerbot/proxy-key.conf
    nginx_reload
    in_home reloaded 60 ./status.sh --check
    expect_exit 0 "the nightly check after a reload, with the worker before still shutting down"
    [ ! -s "$last" ] || problem "the nightly check counted a worker that is shutting down" "$(excerpt)"

    # The public home page: twice, a minute apart, before it's reported; once is a deploy or a setup in progress
    restore deployed
    socket_folder
    in_home page-404 60 env FAKE_PUBLIC_ANSWER=404 ./status.sh --check
    expect_exit 1 "the nightly check with the public home page answering 404"
    [ "$(wc -l < "$last")" = 1 ] && said "The public home page, https://$host_name/, answered 404 twice, a minute apart, where 200 is expected." \
        && said "./setup-dashboard.sh --no-questions --rotate-proxy-key" \
        || problem "the nightly check didn't name the public home page's 404s, and only those" "$(cat "$last")"
    [ "$(public_calls)" = 2 ] || problem "the nightly check didn't ask the public home page twice before it reported it"
    in_home page-522 60 env FAKE_PUBLIC_ANSWER=522 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "answered 522 twice, a minute apart, where 200 is expected, so visitors may not reach the dashboard." \
        || problem "the nightly check didn't name the public home page's 522s, and only those" "$(cat "$last")"
    in_home page-none 60 env FAKE_PUBLIC_ANSWER=000 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "didn't answer within 10 seconds, twice, a minute apart" \
        || problem "the nightly check didn't say the public home page didn't answer, and only that" "$(cat "$last")"
    in_home page-once 60 env FAKE_PUBLIC_ANSWER=404-once ./status.sh --check
    expect_exit 0 "the nightly check with the public home page answering 404 once, then 200"
    [ ! -s "$last" ] || problem "the nightly check reported a 404 that a 200 followed" "$(excerpt)"
    [ "$(public_calls)" = 2 ] || problem "the nightly check didn't ask again after a 404"
    # Bot Fight Mode may challenge a request from a server, which says nothing about the dashboard
    in_home challenge 60 env FAKE_PUBLIC_ANSWER=challenge ./status.sh --check
    expect_exit 0 "the nightly check with a Cloudflare challenge"
    [ ! -s "$last" ] || problem "the nightly check reported a Cloudflare challenge" "$(excerpt)"
    [ "$(public_calls)" = 1 ] || problem "the nightly check didn't ask the public home page exactly once, though a Cloudflare challenge needs no second ask"
    # A stopped dashboard's page is nginx's restarting page: only the stop is reported
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-dashboard
    in_home stopped 60 env FAKE_PUBLIC_ANSWER=404 ./status.sh --check
    [ "$(wc -l < "$last")" = 1 ] && said "The dashboard isn't running." \
        || problem "the nightly check with the dashboard stopped didn't say only that it isn't running" "$(cat "$last")"
    [ "$(public_calls)" = 0 ] || problem "the nightly check asked the public home page while the dashboard is stopped"

    # The plain look
    restore deployed
    socket_folder
    in_home show 60 ./status.sh
    expect_exit 0 "the plain look"
    expect_said "Proxy key: nginx's proxy-key.conf holds the dashboard's key" "the plain look"
    expect_said "Public home page: 200 (https://$host_name/)" "the plain look"
    in_home show-challenge 60 env FAKE_PUBLIC_ANSWER=challenge ./status.sh
    expect_said "Public home page: unknown, since Cloudflare challenged this server's request (cf-mitigated: challenge)" \
        "the plain look with a Cloudflare challenge"
    printf '%s\n' "$other_line" > /etc/nginx/minerbot/proxy-key.conf
    in_home show-other 60 env FAKE_PUBLIC_ANSWER=404 ./status.sh
    expect_said "Proxy key: nginx's /etc/nginx/minerbot/proxy-key.conf doesn't hold the dashboard's proxy key" "the plain look with another key"
    expect_said "Public home page: 404 (https://$host_name/)" "the plain look with another key"

    for f in "$out"/*.out; do
        ! grep -qF -e "$key1" -e "$other_key" "$f" || problem "status.sh printed a proxy key, in ${f##*/}"
    done
    ! tail -n +"$((mark + 1))" "$calls" | grep -q '^restartsrv_nginx ' || problem "status.sh reloaded nginx"
    verdict
}

case_46() {
    begin 46 "status.sh, what keeps nginx's file and the rights working: --check reports, each on one line, a pipelog.conf that doesn't match cPanel's pipelog module, cPanel's hook missing, and a trigger, event or routine in MinerBot's database; a look at the database that fails says nothing; nothing changes"
    local before
    only_line() {   # only_line <run name> <text> <what>: the nightly check said that one line, with exit 1
        in_home "$1" 60 "${@:4}"
        expect_exit 1 "$3"
        [ "$(wc -l < "$last")" = 1 ] && said "$2" || problem "$3: the nightly check didn't say only \"$2\"" "$(cat "$last")"
    }
    restore deployed
    socket_folder
    : > /etc/nginx/minerbot/pipelog.conf
    before=$(home_state)
    only_line pipelog-empty "/etc/nginx/minerbot/pipelog.conf doesn't match cPanel's pipelog module" \
        "pipelog.conf empty while the module loads" ./status.sh --check
    same_state "the nightly check changed something" "$before" "$(home_state)"

    restore deployed
    socket_folder
    rm -f /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog
    only_line no-hook "cPanel's hook /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog is missing" \
        "cPanel's hook missing" ./status.sh --check

    restore deployed
    socket_folder
    only_line extras "MinerBot's database, minerbot_db, holds what no migration makes: event mbtest_sweep, trigger mbtest_stamp." \
        "a trigger and an event in MinerBot's database" env FAKE_DB_EXTRAS=$'trigger mbtest_stamp\nevent mbtest_sweep' ./status.sh --check
    in_home extras-fail 60 env FAKE_DB_EXTRAS=fail ./status.sh --check
    expect_exit 0 "the nightly check when the look at MinerBot's database fails"
    [ ! -s "$last" ] || problem "the nightly check said something when the look at MinerBot's database failed" "$(excerpt)"
    verdict
}

bootstrap_cut() {   # bootstrap_cut <step>: the move's commands, with its detached part killed after that step
    local c
    (cd "$home" && install -d -m 700 state state/tmp logs)
    G -C "$home/src" fetch --prune --prune-tags --force --tags origin
    c=$(G -C "$home/src" rev-parse --verify 'refs/remotes/origin/main^{commit}')
    stage=$(mktemp -d "$home/state/tmp/bootstrap.XXXXXX")
    G -C "$home/src" archive "$c" deploy | tar -x -C "$stage"
    direct "cut-$1" 120 MINERBOT_TEST_CUT_AFTER="$1" -- bash "$stage/deploy/bootstrap.sh" --apply "$c" "$stage"
    [ "$status" = 137 ] || problem "the cut at step $1 didn't end the run with SIGKILL (exit code $status)" "$(excerpt)"
    expect_said "Cut off after step $1 (MINERBOT_TEST_CUT_AFTER)." "the cut at step $1"
    [ -d "$stage" ] || problem "the cut at step $1 removed its staging folder, which a SIGKILL can't"
}

run_case() {   # run_case <number>: the case in a subshell of its own, so one that stops can't stop the others
    local n=$1
    ( "case_$n" )
    if ! grep -qE "^(ok|failed|pending) $n( |\$)" "$results"; then
        printf 'FAILED: case %s: the test itself stopped in this case\n' "$n"
        echo "failed $n" >> "$results"
    fi
}

inside() {
    local n
    # The checks read the calls with grep -q, which can end a pipeline before what feeds it has finished; with pipefail
    # that would count as no match
    set +o pipefail
    umask 022
    export MINERBOT_TEST_FAST=1 TERM=dumb
    rm -rf /out
    mkdir -p /out
    : > "$results"
    prepare
    for n in ${MBTEST_CASES:-$(case_list)}; do
        run_case "$n"
    done
}

if [ "${1:-}" = --inside ]; then
    inside
else
    outside
fi

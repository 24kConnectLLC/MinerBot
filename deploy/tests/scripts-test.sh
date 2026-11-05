#!/usr/bin/env bash
# Checks the logic of MinerBot's server scripts: deploy.sh, rollback.sh, setup-dashboard.sh, fill-secrets.sh, backup.sh,
# status.sh and bootstrap.sh. Nothing else can try them before the server, so this runs them for real in a throwaway
# AlmaLinux 9 container, the server's bash and coreutils, with a real /opt/minerbot and real git, flock, timeout,
# openssl, script and useradd. deploy/tests/fakes holds stand-ins for Docker, MariaDB, nginx, cPanel's reload, systemd,
# curl, ss, sendmail, df and date, and thin wrappers around git and openssl. Each stand-in records its calls in
# /fake/calls.log with the time limit it ran under, and FAKE_* variables make it fail where a case needs that. A local
# bare repository plays GitHub through git's url.<base>.insteadOf, so src's remote.origin.url still reads GitHub's
# address. Each case starts from a saved state of the whole server, so one failure doesn't hide the others.
#
# Run it from Git Bash or Linux: bash deploy/tests/scripts-test.sh. It needs Docker, and pulls almalinux:9 if it isn't
# there. It prints one line per case, then "N of 23 passed", and exits 1 if any case failed. Case 13 ends with the one
# check that uses this machine's Docker directly: Compose's env_file and docker run --env-file must read the same values
# from the dashboard.env setup wrote.
#
# Everything it makes in Docker is named mbtest-scripts-<its PID>-... and removed when it ends. The container's main
# process is sleep 900, so even a run killed before it can clean up leaves nothing running after 15 minutes.
#
# While working on a case: MBTEST_CASES="4 5" runs only those cases (the others count as not run), and
# MBTEST_SAVE=<folder> copies the container's outputs, logs and calls.log there before it's removed.
#
# Each check reads "<condition> && <condition> || problem ...": a problem is noted when any condition fails.
# shellcheck disable=SC2015
set -uo pipefail
umask 077
# Git Bash would otherwise turn the container paths below into Windows paths
export MSYS_NO_PATHCONV=1

cases=23
image=almalinux:9

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
    local here n
    here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
    repo=$(cd "$here/../.." && pwd)
    prefix=mbtest-scripts-$$
    box=$prefix-box
    work=$(mktemp -d)
    passed=0
    trap outside_cleanup EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP

    echo "Starting $box from $image; the cases run inside it."
    if ! timeout -k 10 300 docker run -d --init --name "$box" "$image" sleep 900 > "$work/start.out" 2>&1; then
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
    timeout -k 10 480 docker exec -e MBTEST_CASES="${MBTEST_CASES:-}" "$box" bash /repo/deploy/tests/scripts-test.sh --inside
    timeout -k 10 30 docker exec "$box" cat /out/results > "$work/results" 2> /dev/null || : > "$work/results"
    if [ -n "${MBTEST_SAVE:-}" ]; then
        mkdir -p "$MBTEST_SAVE"
        timeout -k 10 120 docker exec "$box" tar -cf - /out /evidence /fake 2> /dev/null | tar -xf - -C "$MBTEST_SAVE" \
            || echo "Couldn't copy the container's outputs to $MBTEST_SAVE."
    fi
    passed=$(($(awk '$1 == "ok"' "$work/results" | wc -l)))
    for n in $(seq 1 "$cases"); do
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

last_log() {   # last_log <action>: the newest run log of that action
    find "$home/logs" -maxdepth 1 -name "*-$1.log" 2> /dev/null | LC_ALL=C sort | tail -n 1
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

# Ends whatever a case left running: units, stand-ins, scripts, their timeouts and followers, lock holders. AlmaLinux's
# coreutils is one program, so a timeout's command line starts "/usr/bin/coreutils --coreutils-prog-shebang=timeout".
settle() {
    local p pid cmd
    for p in /proc/[0-9]*; do
        pid=${p#/proc/}
        if [ "$pid" = 1 ] || [ "$pid" = "$$" ] || [ "$pid" = "$BASHPID" ]; then continue; fi
        cmd=$(tr '\0' ' ' < "$p/cmdline" 2> /dev/null) || continue
        case $cmd in
            *--mbtest-runner* | *"/usr/local/bin/"* | *"/opt/minerbot/"* | *"/scripts/restartsrv_nginx"* \
                | *"/usr/sbin/sendmail"* | "timeout "* | *"--coreutils-prog-shebang=timeout "* | "flock "* | "script "*)
                kill -KILL "$pid" 2> /dev/null || true
                ;;
        esac
    done
    rm -f /fake/units/*.pid
}

snapshot() {   # snapshot <name>
    tar -C / -cpf "/snap/$1.tar" opt/minerbot etc/nginx etc/cron.d srv fake/docker fake/db fake/nginx work \
        etc/passwd etc/group etc/shadow etc/gshadow || stop "couldn't save the state $1"
}

restore() {   # restore <name>
    settle
    rm -rf /opt/minerbot /etc/nginx /etc/cron.d /srv /fake/docker /fake/db /fake/nginx /work /fake/units /fake/clock \
        /fake/mail /tmp/mbtest-*
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
        "README.txt $home/README.txt 600" "minerbot.cron /etc/cron.d/minerbot 644"
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

# ---- Building the states

prepare() {
    local f name
    echo "Installing what the server has (dnf), and the stand-ins."
    if ! rpm -q git-core util-linux procps-ng diffutils openssl jq shadow-utils > /dev/null 2>&1; then
        timeout -k 10 240 dnf -y -q install git-core util-linux procps-ng diffutils openssl jq shadow-utils > /out/dnf.log 2>&1 \
            || stop "dnf couldn't install the packages: $(tail -n 5 /out/dnf.log)"
    fi
    # A clean start, also when this runs again in the same container
    rm -rf /work /opt/minerbot /etc/nginx /srv /fake /evidence /snap /etc/cron.d/minerbot /root/.gitconfig /tmp/mbtest-*
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
    cp /work/certs/good-cert.pem /fake/nginx/served-cert.pem
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
    refused option "Unknown option --fast." ./deploy.sh --fast
    refused only-foo "--only takes bot or dashboard." ./deploy.sh --only foo
    mv "$home/server.conf" /tmp/mbtest-server.conf
    refused no-conf "$home/server.conf is missing." ./deploy.sh --only bot
    mv /tmp/mbtest-server.conf "$home/server.conf"
    sed -i 's/^SITE_DB=.*/SITE_DB=web-site/' "$home/server.conf"
    refused bad-name "SITE_DB in $home/server.conf must be the website's database name" ./deploy.sh --only bot
    sed -i 's/^SITE_DB=.*/SITE_DB=website_db/' "$home/server.conf"
    refused no-env "There's no dashboard.env yet." ./deploy.sh
    expect_said "To deploy only the bot meanwhile: --only bot." no-env
    hold_lock "$home/state/lock"
    echo "setup, started 2026-11-06T03:00:00Z, log $home/logs/20261106T030000Z-setup.log" > "$home/state/lock.info"
    refused lock "Another run holds the lock: setup, started 2026-11-06T03:00:00Z" ./deploy.sh --only bot
    let_go
    same_state "the refusals in the foreground changed files" "$before" "$(home_state)"

    in_home not-main 60 ./deploy.sh feature --only bot --no-start
    expect_exit 1 "a commit not on main"
    expect_said "isn't merged into main" "a commit not on main"
    if run_calls | grep -qE '^docker \[[^]]*\] build '; then problem "a commit not on main: it built something"; fi

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
    verdict
}

case_3() {
    begin 3 "deploy files: a commit that changes deploy.sh is installed and the run goes on once with the new code, building exactly that commit while main moves on; a run with MINERBOT_REEXEC never runs again; an older commit keeps the newer files; a script that fails bash -n stops it with nothing installed"
    local log before
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
    verdict
}

case_4() {
    begin 4 "a full deploy: the settings checked, the rights rehearsed, applied and compared, then the dashboard swapped and ready (its own process on the port, the public check) before the bot; history ok"
    local log dash bot
    restore deployed
    set_main "$c5"
    in_home full 120 ./deploy.sh
    expect_exit 0 "the full deploy"
    log=$(last_log deploy)
    in_order "the order of a full deploy" \
        "^docker \[timeout -k 10 60\] run .* --env-file /opt/minerbot/dashboard.env minerbot-dashboard:new check-settings\$" \
        "^mariadb \[timeout -k 10 30\] --batch -e DROP USER IF EXISTS 'minerbot-rights-check'@'localhost'; CREATE USER 'minerbot-rights-check'@'localhost' ACCOUNT LOCK;" \
        '^mariadb \[timeout -k 10 60\] --batch \{stdin: rehearsal\}' \
        '^mariadb \[timeout -k 10 60\] --batch \{stdin: apply\}' \
        '\{stdin: rights of minerbot-rights-check@localhost\}' \
        "--batch -e DROP USER IF EXISTS 'minerbot-rights-check'@'localhost'\$" \
        '\{stdin: rights of minerbot-dashboard@localhost\}' \
        '\{stdin: rights of minerbot-dashboard@127.0.0.1\}' \
        '^curl \[timeout -k 10 15\] -s -o /dev/null -w %\{http_code\} --max-time 10 https://bot.murderminershub.com/$' \
        '^docker \[timeout -k 10 120\] compose -f /opt/minerbot/compose.yml --project-directory /opt/minerbot up -d --no-build --no-deps dashboard$' \
        '^curl \[timeout -k 10 10\] -s --max-time 5 -H Host: localhost http://127.0.0.1:5080/health/ready$' \
        "^ss \[timeout -k 10 10\] -Hltnp sport = :5080\$" \
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
    expect_said "The dashboard is ready: /health/ready answers Healthy, and port 5080 is its own." "the full deploy" "$log"
    expect_said "The public home page answers 200, as before the swap." "the full deploy" "$log"
    expect_said "The bot is ready, and still runs." "the full deploy" "$log"
    grep -qP "\tdeploy\tdashboard\t${c5:0:12}\tok\twas ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the dashboard's swap"
    grep -qP "\tdeploy\tbot\t${c5:0:12}\tok\twas ${c1:0:12}" "$home/logs/history.log" || problem "no history line for the bot's swap"
    tail -n 1 "$home/logs/history.log" | grep -qP "\tdeploy\tdashboard,bot\t${c5:0:12}\tok" || problem "the run's closing history line isn't ok"
    verdict
}

case_5() {
    begin 5 "the same commit again: both services are already on it, and no tag moves, :previous included; restarts from before the deploy don't count, whether Compose leaves the container running or starts it"
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
    expect_said "The dashboard is ready: /health/ready answers Healthy, and port 5080 is its own." "$what"
    expect_said "The bot is ready, and still runs." "$what"
    ! said "its container restarted" || problem "$what: a restart from before the deploy counted as one" "$(excerpt)"
    [ "$(cut -d' ' -f2,3 /fake/docker/ctr/minerbot-dashboard)" = "true 0" ] || problem "$what: the dashboard wasn't started"
    verdict
}

case_6() {
    begin 6 "the dashboard not ready (another process on its port): back on its exact image, :failed set, :previous as before, its log saved 600 and never shown, the bot untouched, RESULT failed"
    local bot_before failed log
    restore deployed
    set_main "$c5"
    bot_before=$(fingerprint /fake/docker/ctr/minerbot-bot /fake/docker/tags/minerbot-bot@current /fake/docker/tags/minerbot-bot@previous)
    in_home unready 120 env FAKE_SS_FOREIGN="dashboard@${c5:0:12}" ./deploy.sh
    expect_exit 1 "the unready dashboard"
    log=$(last_log deploy)
    expect_said "the process listening on 127.0.0.1:5080 isn't the dashboard's container" "the unready dashboard" "$log"
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
    begin 9 "the first dashboard swap not ready: the dashboard is stopped and removed, :current taken away and :failed set"
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
    begin 12 "rollback.sh toggles by image ID: back, then forward again; it refuses when there's nothing to go back to, and prints the usage without a service"
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
    verdict
}

case_13() {
    begin 13 "the first setup through a real terminal: CRLF pastes with the key never shown, the Access team tidied and its keys checked, the login made through stdin, the rights, dashboard.env 600 in the exact format, nginx's files and modes, the standalone test with the main context and then one reload, the 403 check, no dashboard started; and both of Docker's readers take dashboard.env the same way"
    local cert key env_key value line name
    restore built
    cert=$(paste_of /work/certs/good-cert.pem)$'\n'
    key=$(paste_of /work/certs/good-key.pem)$'\n'
    converse setup-first "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "Paste the Origin certificate" "$cert" \
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
    said "$(sed -n 2p /work/certs/good-cert.pem)" || problem "the certificate's paste isn't in the transcript, so the test didn't type it"
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
    begin 15 "setup converges: Enter and keep change nothing and only add an ok line; an admin-list change only recreates; --rotate-proxy-key stops the dashboard before the reload and recreates it after; a wrong current password gets a new one in both places"
    local env_before nginx_before lines key pw
    restore deployed
    env_before=$(sha256sum < "$home/dashboard.env")
    nginx_before=$(fingerprint /etc/nginx)
    lines=$(wc -l < "$home/logs/history.log")
    converse keep "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "Paste the Origin certificate" "keep"$'\r' \
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

    restore deployed
    nginx_before=$(fingerprint /etc/nginx)
    converse admins "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "Paste the Origin certificate" "keep"$'\r' \
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
    begin 16 "setup refuses, each with its message: a wildcard certificate, a two-name one, one ending within 30 days, a key that doesn't match, an encrypted key, a bad client ID, a secret with \$, ' #' or a space at the end, an ID on both lists, a bad AUD; and changes nothing"
    local before mark
    restore deployed
    before=$(home_state)
    mark=$(wc -l < "$calls")
    converse certificates "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "Paste the Origin certificate" "$(paste_of /work/certs/wildcard-cert.pem)"$'\n' \
        "Paste the Origin certificate" "$(paste_of /work/certs/twoname-cert.pem)"$'\n' \
        "Paste the Origin certificate" "$(paste_of /work/certs/short-cert.pem)"$'\n'
    expect_exit 1 "three wrong certificates"
    [ "$(grep -c 'That certificate must name bot.murderminershub.com and nothing else' "$last")" = 2 ] \
        || problem "the wildcard and two-name certificates weren't both refused for their names" "$(excerpt)"
    expect_said "That certificate ends within 30 days." "the short certificate"
    expect_said "Three answers in a row weren't right. Nothing was changed." "three wrong certificates"

    # shellcheck disable=SC2016 # a $ the secret must not have
    converse keys "env MINERBOT_TEST_READ_LIMIT=30 $home/setup-dashboard.sh" \
        "Paste the Origin certificate" "$(paste_of /work/certs/good-cert.pem)"$'\n' \
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
        "Paste the Origin certificate" "keep"$'\r' \
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
    verdict
}

case_17() {
    begin 17 "backup.sh: a failing dump, or one without its last line, leaves no file and one line on stderr; a working one writes a 600 dump and prunes to 14 days, the newest 3 kept; pre-deploy takes only the backup lock; a run killed from outside is mailed by the cron line and reported by the nightly check"
    local dumps_before line cmd stamp expected=''
    backup_run() {   # backup_run <name> <seconds> <command...>: stdout and stderr apart
        local name=$1 limit=$2
        shift 2
        run_mark=$(wc -l < "$calls")
        (cd "$home" && timeout -k 5 "$limit" "$@") < /dev/null > "$out/$name.out" 2> "$out/$name.err"
        status=$?
        last=$out/$name.err
    }
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

    # The cron line, its limit cut to 3 seconds, while the dump hangs. bash 5.1, the server's, runs backup.sh's EXIT
    # trap when timeout ends it there, so backup.log may say failed; either way cron mails the echo, and the check reports it.
    restore deployed
    line=$(grep 'backup.sh nightly' /etc/cron.d/minerbot)
    [[ $line == *" root timeout -k 60 3600 /opt/minerbot/backup.sh nightly || echo "* ]] || problem "the cron line isn't the designed one" "$line"
    cmd=${line#* root }
    cmd=${cmd/timeout -k 60 3600/timeout -k 2 3}
    backup_run killed 30 env FAKE_DUMP=hang /bin/bash -c "$cmd"
    said "MinerBot's nightly backup failed or was stopped at its time limit." "$out/killed.out" \
        || problem "the cron line's echo didn't run" "$(cat "$out/killed.out" "$out/killed.err")"
    settle
    stamp=$(date -u -d '-3 hours' +%Y-%m-%dT%H:%M:%SZ)
    sed -i "\$s/^[^ ]* started nightly\$/$stamp started nightly/" /srv/backups/minerbot/backup.log
    case $(tail -n 1 /srv/backups/minerbot/backup.log) in
        *" started nightly") expected="A backup started at $stamp and never finished" ;;
        *" failed dumping") expected="The last backup failed" ;;
        *) problem "backup.log's last line is neither the started line nor a failed line" "$(tail -n 2 /srv/backups/minerbot/backup.log)" ;;
    esac
    in_home check 60 ./status.sh --check
    expect_said "$expected" "the nightly check after the run stopped at the cron line's limit"

    # Killed outright, as a restart would: no trap can run, so the started line stays alone until the check reports it
    restore deployed
    # timeout -s KILL ends its own process group, itself included, so it runs under a shell of its own
    backup_run sigkill 30 env FAKE_DUMP=hang bash -c 'timeout -s KILL 3 ./backup.sh nightly'
    [ "$status" = 137 ] || problem "the killed backup ended with exit code $status, not SIGKILL's 137"
    tail -n 1 /srv/backups/minerbot/backup.log | grep -q ' started nightly$' \
        || problem "backup.log's last line isn't the started line" "$(tail -n 2 /srv/backups/minerbot/backup.log)"
    settle
    stamp=$(date -u -d '-3 hours' +%Y-%m-%dT%H:%M:%SZ)
    sed -i "\$s/^[^ ]* started nightly\$/$stamp started nightly/" /srv/backups/minerbot/backup.log
    in_home check-sigkill 60 ./status.sh --check
    expect_said "A backup started at $stamp and never finished" "the nightly check after the killed backup"
    verdict
}

case_18() {
    begin 18 "status.sh: --check names each seeded problem on its own line with exit 1, and is silent with exit 0 when all is well, never reloading nginx; --nginx-look never prints the proxy key or a root-only file"
    local f
    restore deployed
    in_home fine 60 ./status.sh --check
    expect_exit 0 "the nightly check when all is well"
    [ ! -s "$last" ] || problem "the nightly check printed something when all is well" "$(excerpt)"

    restore deployed
    touch -d '-40 hours' /srv/backups/minerbot/*.sql.gz
    install -m 600 /work/certs/short-cert.pem /etc/nginx/minerbot/origin-cert.pem
    sed -i 's/^\(\S* \)true /\1false /' /fake/docker/ctr/minerbot-bot
    sed -i 's/ healthy / unhealthy /' /fake/docker/ctr/minerbot-dashboard
    : > /etc/nginx/conf.d/includes-optional/cloudflare.conf
    echo "$home/state/nginx-backup/20261106T000000Z" > "$home/state/nginx-change"
    in_home problems 60 env FAKE_NGINX_T=fail FAKE_SS=foreign FAKE_DF=/srv=1048576 ./status.sh --check
    expect_exit 1 "the nightly check with problems"
    [ "$(wc -l < "$last")" = 9 ] || problem "the nightly check didn't print exactly 9 lines for the 9 problems" "$(cat "$last")"
    for f in "hours old." "within 30 days" "nginx -t fails, twice a minute apart" "The bot isn't running." "The dashboard is unhealthy." \
        "Something other than the dashboard's container listens on port 5080" "is empty, so nginx refuses every visitor" \
        "An nginx change was cut off." "Less than 5 GiB is free on /srv"; do
        expect_said "$f" "the nightly check with problems"
    done
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

# Every call of a command that must go through run_for, where it stands at a command position: a line's start, or after
# |, ;, &, (, {, !, $(, then, do or else, with or without a path. Comment lines and run_for itself are left out. Each
# line is matched as it is, so ^ is the line's own start. Prints file:line: text.
unlimited_calls() {   # unlimited_calls <file>...
    awk -v words='docker|mariadb|mariadb-dump|curl|git|openssl|nginx|ss|sendmail|systemctl|systemd-run|restartsrv_nginx' '
        /^run_for\(\) \{/ { skip = 1 }
        skip && /^\}/ { skip = 0; next }
        skip || /^[[:space:]]*#/ { next }
        $0 ~ ("(^|[|;&({!]|(^|[^A-Za-z0-9_])(then|do|else)[[:space:]])[[:space:]]*([^[:space:]]*/)?(" words ")([[:space:]]|$)") {
            name = FILENAME
            sub(/.*\//, "", name)
            print name ":" FNR ": " $0
        }' "$@"
}

case_20() {
    begin 20 "static rules: set -Eeuo pipefail and umask 077 in every script, no tracing, every listed command under run_for (a check that finds each kind of position in a copy of common.sh), every read with -t, every stty behind on_terminal; across every case, no call without a time limit and no password statement without --skip-print-query-on-error; every script and stand-in recorded as 100755"
    local f found
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
        '    { systemctl status; }')
    { cat /repo/deploy/common.sh; printf '%s\n' "${probes[@]}"; } > /tmp/mbtest-calls.sh
    found=$(unlimited_calls /tmp/mbtest-calls.sh)
    [ "$(printf '%s' "$found" | grep -c .)" = "${#probes[@]}" ] \
        || problem "the check for calls outside run_for didn't find exactly the ${#probes[@]} added to a copy of common.sh" "$found"
    printf '%s\n' '    run_for 30 docker ps' '    echo "the docker ps above"' '    local docker_id=1' '    # docker ps' \
        '    git_url=x' > /tmp/mbtest-calls.sh
    found=$(unlimited_calls /tmp/mbtest-calls.sh)
    [ -z "$found" ] || problem "the check for calls outside run_for flags lines that are fine" "$found"
    found=$(grep -nE '(^|[|;&({!]|(^|[^A-Za-z0-9_])(then|do|else|while|until|if)[[:space:]])[[:space:]]*(IFS=[^[:space:]]*[[:space:]]+)?read[[:space:]]' \
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
timeout 120 git -C src fetch --quiet --prune --tags origin
c=$(timeout 10 git -C src rev-parse --verify 'origin/main^{commit}')
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

bootstrap_cut() {   # bootstrap_cut <step>: the move's commands, with its detached part killed after that step
    local c
    (cd "$home" && install -d -m 700 state state/tmp logs)
    G -C "$home/src" fetch --quiet --prune --tags origin
    c=$(G -C "$home/src" rev-parse --verify 'origin/main^{commit}')
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
    for n in ${MBTEST_CASES:-1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 21 22 23 19 20}; do
        run_case "$n"
    done
}

if [ "${1:-}" = --inside ]; then
    inside
else
    outside
fi

#!/usr/bin/env bash
# Sourced by every stand-in in deploy/tests/fakes, which scripts-test.sh installs in its throwaway AlmaLinux container.
# record writes one line per call to /fake/calls.log: the stand-in's name, the time limit its parent timeout was given,
# its arguments, and the names (never the values) of its environment. When the parent process isn't timeout, it also
# writes a NO-LIMIT line, which scripts-test.sh counts as a failure: every call the scripts make must go through run_for.
# shellcheck shell=bash

fake=/fake
calls=$fake/calls.log
D=$fake/docker

# How the parent process was started when it's timeout, as in "timeout -k 10 30"; otherwise "parent <its name>".
# AlmaLinux's coreutils is one program, so timeout's command line can start "/usr/bin/coreutils
# --coreutils-prog-shebang=timeout /usr/bin/timeout": its options come after the word that names timeout.
parent_limit() {
    local comm='' out=timeout i=0
    local -a args=()
    { read -r comm < "/proc/$PPID/comm"; } 2> /dev/null || comm=unknown
    if [ "$comm" != timeout ]; then
        printf 'parent %s\n' "$comm"
        return 0
    fi
    { mapfile -d '' -t args < "/proc/$PPID/cmdline"; } 2> /dev/null || true
    while [ "$i" -lt "${#args[@]}" ] && [ "${args[$i]##*/}" != timeout ]; do
        i=$((i + 1))
    done
    i=$((i + 1))
    while [ "$i" -lt "${#args[@]}" ]; do
        case ${args[$i]} in
            -k | -s | --kill-after | --signal)
                out+=" ${args[$i]} ${args[$((i + 1))]:-}"
                i=$((i + 2))
                ;;
            -*)
                out+=" ${args[$i]}"
                i=$((i + 1))
                ;;
            *)
                out+=" ${args[$i]}"
                break
                ;;
        esac
    done
    printf '%s\n' "$out"
}

record() {   # record <name> <arguments...>; $note, when set, is added in braces, such as {stdin: rehearsal}
    local name=$1 limit names extra=''
    shift
    limit=$(parent_limit)
    names=$(compgen -e)
    if [ -n "${note:-}" ]; then
        extra=" {$note}"
    fi
    printf '%s [%s] %s%s ##env %s\n' "$name" "$limit" "$*" "$extra" "${names//$'\n'/ }" >> "$calls"
    case $limit in
        timeout*) ;;
        *) printf 'NO-LIMIT %s %s\n' "$name" "$*" >> "$calls" ;;
    esac
}

# Waits up to <seconds> without starting a process of its own, so nothing is left behind when the stand-in is killed
block() {   # block <seconds>
    local _
    read -r -t "$1" _ <> <(:) || true
}

# The service and the first 12 characters of the revision label of the image a container runs, as in
# dashboard@1a2b3c4d5e6f, which FAKE_UNREADY and the other FAKE_* lists name; nothing when there's no such container
running_key() {   # running_key bot|dashboard
    local image='' rest label=''
    { read -r image rest < "$D/ctr/minerbot-$1"; } 2> /dev/null || return 0
    if [ -f "$D/ids/$image" ]; then
        read -r label < "$D/ids/$image" || true
    fi
    printf '%s@%s\n' "$1" "${label:0:12}"
}

listed() {   # listed <space-separated list> <word>: true when the word is in the list
    [[ " $1 " == *" $2 "* ]]
}

# The KEY=VALUE lines of a container's env file, as Compose gives them to the container it makes
env_of() {   # env_of <container name>
    local file=/opt/minerbot/bot.env
    if [ "$1" = minerbot-dashboard ]; then
        file=/opt/minerbot/dashboard.env
    fi
    if [ -f "$file" ]; then
        grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$file" || true
    fi
}

# The environment a container runs with, as docker container inspect's {{range .Config.Env}} prints it: its env file's
# lines when the stand-in docker made it, or, for a container it didn't make, such as the made-up states', its env
# file's lines as they are now
container_env() {   # container_env <container name>
    if [ -f "$D/env/$1" ]; then
        cat "$D/env/$1"
    else
        env_of "$1"
    fi
}

running() {   # running bot|dashboard: true when that container exists and runs
    local image state='' rest
    { read -r image state rest < "$D/ctr/minerbot-$1"; } 2> /dev/null || return 1
    [ "$state" = true ]
}

# The dashboard's socket, as its Kestrel keeps it: a real socket file, made as uid 1654 makes it in the setgid folder
# (owner 1654, group nobody, mode 660), there while the dashboard's container runs, unless its image is in FAKE_NO_SOCKET
# (dashboard@<rev>), and gone once it doesn't run. The folder is systemd-tmpfiles' to make; without it there's no socket.
# The stand-ins docker and curl call this first, so the scripts always see the socket as the container left it.
sync_socket() {
    local dir=/run/minerbot-dashboard sock=/run/minerbot-dashboard/dashboard.sock
    [ -d "$dir" ] || return 0
    if running dashboard && ! listed "${FAKE_NO_SOCKET:-}" "$(running_key dashboard)"; then
        if [ ! -e "$sock" ]; then
            python3 -c 'import socket, sys; socket.socket(socket.AF_UNIX).bind(sys.argv[1])' "$sock" 2> /dev/null \
                && chown 1654 "$sock" && chmod 660 "$sock"
        fi
    elif [ -S "$sock" ]; then
        rm -f "$sock"
    fi
    return 0
}

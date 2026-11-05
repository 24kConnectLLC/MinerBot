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

running() {   # running bot|dashboard: true when that container exists and runs
    local image state='' rest
    { read -r image state rest < "$D/ctr/minerbot-$1"; } 2> /dev/null || return 1
    [ "$state" = true ]
}

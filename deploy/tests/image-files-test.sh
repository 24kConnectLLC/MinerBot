#!/usr/bin/env bash
# Checks that the bot's and the dashboard's images let their own user, 1654, read every file in /app, and let it own
# and change none of them, when the images are built from files only root can read. deploy.sh's checkout in
# /opt/minerbot/src wrote every file it changed that way under umask 077, until it checked out under umask 022, and an
# src from then still holds such files. A build copies the files' modes into the image, and dotnet publish keeps a
# static file's mode, so before the Dockerfiles' chmod the dashboard's stylesheet was root's alone in the image, and
# every page answered 500.
#
# Run it from Git Bash or Linux: bash deploy/tests/image-files-test.sh. It needs Docker, git and GNU tar. It builds both
# images from the repository's tracked files as the working tree has them, committed or not, packed as that checkout
# left them: files 600 or 700, folders 700, owned by root. Docker applies no .dockerignore to a tar on standard input,
# so this context also holds what .dockerignore keeps out (deploy/, .github, README.md, the Dockerfiles) and lacks the
# .git files it lets in; none of them reaches /app. It prints one line per check, then "N of M passed", and exits 1 if
# any check failed. Each build gets 15 minutes.
#
# Its two images, mbtest-files-<its PID>-bot and -dashboard, labelled mbtest-files, and its scratch folder,
# mbtest-files.XXXXXX in TMPDIR, are removed when it ends. Their build cache stays, so the next run is quicker, and
# docker builder prune removes it. A run killed during a build can leave its folder and, once that build ends, its
# images: docker image prune -a -f --filter label=mbtest-files removes them.
set -Eeuo pipefail
umask 077
# Git Bash would otherwise turn the container paths below into Windows paths
export MSYS_NO_PATHCONV=1

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
prefix=mbtest-files-$$
checks=5   # how many checks this script makes
made=0
passed=0
notes=""
work=""

cleanup() {
    local left
    timeout -k 10 120 docker image rm -f "$prefix-bot" "$prefix-dashboard" > /dev/null 2>&1 || true
    left=$(timeout -k 10 60 docker image ls -q --filter "reference=$prefix-*" 2> /dev/null || true)
    if [ -n "$left" ]; then
        echo "This run's images are still there. Remove them with: docker image rm -f $prefix-bot $prefix-dashboard"
    fi
    cd /
    if [ -n "$work" ]; then rm -rf "$work" || true; fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

stop() {   # stop <why>: ends the run here; the checks it didn't make count as failed
    echo "STOPPED: $1"
    echo "$passed of $checks passed"
    exit 1
}

problem() {   # problem <what's wrong> [<what came back>]: notes why the current check fails
    notes+="    $1"$'\n'
    if [ -n "${2:-}" ]; then
        notes+=$(sed -n '1,40{s/\r$//;s/^\(.\{160\}\).*/\1 .../;s/^/    | /;p}' <<< "$2")$'\n'
    fi
}

verdict() {   # verdict <message>: ends one check, which passed if it noted no problem
    made=$((made + 1))
    if [ -z "$notes" ]; then
        passed=$((passed + 1))
        echo "ok: $1"
    else
        echo "FAILED: $1"
        printf '%s' "$notes"
        notes=""
    fi
}

# The tracked files, as the working tree has them, copied to a scratch folder and packed with only their owner's rights
# and root as their owner. A tar on standard input is the one build context whose modes Docker takes as they are, even
# from Windows, where a folder's files would all arrive as 755.
pack() {
    local status=0
    work=$(mktemp -d "${TMPDIR:-/tmp}/mbtest-files.XXXXXX")
    mkdir "$work/files"
    (cd "$repo" && timeout -k 10 60 git ls-files -z) > "$work/listed" \
        || stop "git couldn't list the repository's files."
    # A file deleted in the working tree stays listed until the deletion is committed, so only the files that exist
    (
        cd "$repo"
        while IFS= read -r -t 5 -d '' f; do
            [ ! -f "$f" ] || printf '%s\0' "$f"
        done < "$work/listed"
    ) > "$work/present"
    # Every archive goes through standard input or output: GNU tar takes a name with a colon, as TMPDIR=G:/Temp gives
    # in Git Bash, for a remote host's
    (cd "$repo" && timeout -k 10 120 tar --null -T - -cf - < "$work/present") \
        | timeout -k 10 120 tar -xf - -C "$work/files" || status=$?
    [ "$status" = 0 ] || stop "Copying the tracked files to the scratch folder failed (exit code $status)."
    timeout -k 10 120 tar -C "$work/files" --mode=go-rwx --owner=0 --group=0 --numeric-owner -cf - . \
        > "$work/context.tar" || stop "Packing the build context failed."
}

# The context must be what that checkout left, or the checks after it prove nothing
check_context() {
    local listing odd css
    listing=$(timeout -k 10 60 tar -tvf - < "$work/context.tar") || stop "tar couldn't list the build context."
    odd=$(awk '($1 != "-rw-------" && $1 != "-rwx------" && $1 != "drwx------") || $2 != "0/0"' <<< "$listing")
    if [ -n "$odd" ]; then
        problem "These entries aren't their owner's alone, or aren't root's" "$odd"
    fi
    css=$(grep -E ' \./MinerBot\.Dashboard/wwwroot/css/site\.css$' <<< "$listing") || css=""
    case $css in
        "-rw------- 0/0 "*) ;;
        *) problem "The dashboard's stylesheet isn't in the context as a file only root can read" "$css" ;;
    esac
    verdict "the build context holds files 600 or 700 and folders 700, all root's, as the checkout under umask 077 left them"
}

build() {   # build bot|dashboard: mbtest-files-<PID>-<service> from the packed context
    local dockerfile=MinerBot.Dashboard/Dockerfile out status=0
    if [ "$1" = bot ]; then
        dockerfile="MinerBot 2.0/Dockerfile"
    fi
    echo "Building the $1's image from the packed files, within 15 minutes."
    # Plain progress, so a failed build's last lines hold the failing step's own output
    out=$(timeout -k 30 900 docker build --progress=plain --label mbtest-files -f "$dockerfile" -t "$prefix-$1" - \
        < "$work/context.tar" 2>&1) || status=$?
    if [ "$status" != 0 ]; then
        stop "The $1's image didn't build (exit code $status). The end of its output:"$'\n'"$(tail -n 30 <<< "$out")"
    fi
}

in_app() {   # in_app bot|dashboard <find arguments...>: find in the image's /app, as the image's own user
    local service=$1
    shift
    timeout -k 10 120 docker run --rm --network none --entrypoint find "$prefix-$service" /app "$@" 2>&1
}

check_image() {   # check_image bot|dashboard <its volume's folder>: two checks, as the image's own user
    local service=$1 volume=$2 user out status=0
    user=$(timeout -k 10 60 docker image inspect -f '{{.Config.User}}' "$prefix-$service" 2>&1) \
        || stop "Docker couldn't inspect the $service's image: $user"
    if [ "$user" != 1654 ] && [ "$user" != 1654:1654 ]; then
        problem "The $service's image runs as '$user', not 1654, so this check wouldn't be the app's view"
    fi
    out=$(in_app "$service" ! -readable -print) || status=$?
    if [ "$status" != 0 ] || [ -n "$out" ]; then
        problem "find exited $status, listing what the $service's user can't read or open" "$out"
    fi
    verdict "the $service's user, 1654, can read every file and open every folder in /app"

    # Anything not root's counts too: its owner can make it writable again, which needs no capability
    status=0
    out=$(in_app "$service" -path "$volume" -prune -o \( -writable -o ! -uid 0 \) -print) || status=$?
    if [ "$status" != 0 ] || [ -n "$out" ]; then
        problem "find exited $status, listing what the $service's user owns or could change" "$out"
    fi
    # Its volume's folder must stay its own, since a new volume takes the folder's owner: the dashboard keeps its
    # sign-in keys in /app/keys, and the bot its data and Steam sign-in in /app/data
    status=0
    out=$(in_app "$service" -path "$volume" -writable -print -prune) || status=$?
    if [ "$status" != 0 ] || [ "$out" != "$volume" ]; then
        problem "find exited $status: the $service's user can't write to $volume, its volume's folder" "$out"
    fi
    verdict "the $service's user owns and can change nothing in /app but $volume, its volume's folder, which it can write to"
}

pack
check_context
build bot
check_image bot /app/data
build dashboard
check_image dashboard /app/keys

echo "$passed of $checks passed"
if [ "$made" != "$checks" ]; then
    echo "This script made $made checks, but checks= at its top says $checks. Correct that count."
    exit 1
fi
[ "$passed" = "$checks" ] || exit 1

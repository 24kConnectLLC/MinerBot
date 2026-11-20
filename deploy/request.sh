#!/usr/bin/env bash
# Sends the bot one of the dashboard's requests from the server, through the dashboard's own code: the live check of the
# bot's requests before the dashboard has a page for them, and a way to reach the bot when the pages can't. Run as root.
# It runs the dashboard's image once, as its request command, with the dashboard's own database login, the
# request-signing key and the owners from dashboard.env, and the request's text, each passed by name from this script's
# environment, never shown and never in the one-off's command line. The request says the server asked, as the dashboard's
# first Owner, named request.sh on the server. This script holds the lock until the bot has answered, so no deploy or
# setup runs meanwhile, and after a restart the command waits until the bot is back on Discord.
# Usage:
#   ./request.sh <kind> [--guild <server ID>] [--channel <channel ID>] [--text <text>]
#   ./request.sh --list        the kinds, who may ask for each, their limits and how long each waits, from the image
# Exit code: 0 done, and after a restart the bot back on Discord; 1 refused, failed, expired, unknown, no answer, or
# stopped before anything was sent, with the reason; 2 the request command refused an argument or a setting, and
# nothing was sent.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

action=request
subject=-
step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: ./request.sh <kind> [--guild <server ID>] [--channel <channel ID>] [--text <text>], or ./request.sh --list"
image=minerbot-dashboard:current
# The one-off, removed before it starts and after it ends, since a run killed at its limit can leave it behind. --list,
# which takes no lock, has a name of its own, so it never removes a request's one-off that waits for the bot's answer.
helper=minerbot-request
# The longest a request takes: a 10-minute expiry, the bot's longest action, or a restart's 2 minutes and the 6 the
# command then waits for the bot to be back, whichever is longer, with room to spare
limit=900

kind=
list=no
text=
text_given=no
options=()
while [ $# -gt 0 ]; do
    case $1 in
        --list) list=yes ;;
        --guild | --channel)
            # The request command's own rules, checked here first, so a mistake gets this script's usage, not the
            # command's, which names its environment
            [ $# -ge 2 ] || die "$1 needs an ID. $usage"
            [[ $2 =~ ^[0-9]{17,20}$ ]] || die "$1 takes a numeric Discord ID, 17 to 20 digits. $usage"
            [[ " ${options[*]} " != *" $1 "* ]] || die "$1 goes once. $usage"
            options+=("$1" "$2")
            shift
            ;;
        --text)
            { [ $# -ge 2 ] && [ "$text_given" = no ]; } || die "--text goes once, with the text after it. $usage"
            text=$2
            text_given=yes
            shift
            ;;
        -*) die "$usage" ;;
        *)
            [ -z "$kind" ] || die "$usage"
            kind=$1
            ;;
    esac
    shift
done
if [ "$list" = yes ] && { [ -n "$kind" ] || [ ${#options[@]} -gt 0 ] || [ "$text_given" = yes ]; }; then
    die "--list goes alone. $usage"
fi
if [ "$list" = no ] && ! [[ $kind =~ ^[a-z_]{1,30}\.[a-z_]{1,30}$ ]]; then
    die "Name a kind of request, such as caches.clear: ./request.sh --list shows them. $usage"
fi

out=
helper_started=no
# shellcheck disable=SC2317,SC2329 # the EXIT trap runs it
cleanup() {
    if [ "$helper_started" = yes ]; then
        run_for 30 docker rm -f -v "$helper" > /dev/null 2>&1 || true
    fi
    if [ -n "$out" ]; then
        rm -f "$out"
    fi
}

# The image once, as its request command, on the network given. Only the settings named with -e in $passed go in, by
# name, from the values this script set for this one command. The image's health check, which asks for a dashboard that
# doesn't run in the one-off, is left out. The output goes to the screen as it comes, and to $out for the lines read
# after; the exit code is in $ended.
one_off() {   # one_off <network> <request command arguments...>
    local network=$1
    shift
    run_for 30 docker rm -f -v "$helper" > /dev/null 2>&1 || true
    helper_started=yes
    ended=0
    ConnectionStrings__BotConnection=${connection:-} Dashboard__RequestSigningKey=${key:-} Dashboard__Owners=${owners:-} \
        MINERBOT_REQUEST_TEXT=$text run_for "$limit" docker run --rm --pull never --name "$helper" --network "$network" \
        --no-healthcheck --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --cap-drop ALL \
        --security-opt no-new-privileges:true --memory 256m --pids-limit 64 "${passed[@]}" "$image" request "$@" 2>&1 \
        | tee "$out" || ended=$?
    run_for 30 docker rm -f -v "$helper" > /dev/null 2>&1 || true
    helper_started=no
}

# An image from before the request command starts the web host instead, which stops at once on its settings
too_old() {
    if grep -qF "The dashboard didn't start, because of its settings:" "$out"; then
        echo "The dashboard's image doesn't know the request command: deploy PR 9's dashboard first."
        return 0
    fi
    return 1
}

step="checking before it sends"
[ "$(id -u)" = 0 ] || die "Run this as root. Nothing was sent."
cd "$home"
install -d -m 700 "$home/state" "$home/state/tmp"
trap 'status=$?; cleanup || true; exit "$status"' EXIT
out=$(mktemp "$home/state/tmp/request.XXXXXX")

if [ "$list" = yes ]; then
    [ -n "$(image_id "$image")" ] || die "There's no dashboard image, $image, to read the list from: run ./deploy.sh first."
    helper=minerbot-request-list
    passed=()
    one_off none --list
    if too_old; then
        exit 1
    fi
    exit "$ended"
fi

[ -f "$home/dashboard.env" ] \
    || die "dashboard.env is missing, so the dashboard isn't set up and has no request-signing key. Run ./setup-dashboard.sh first. Nothing was sent."
key_problems=$(request_key_problems)
[ -z "$key_problems" ] || die "$key_problems
Nothing was sent."
[ -n "$(image_id "$image")" ] || die "There's no dashboard image, $image, to send the request with. Run ./deploy.sh first. Nothing was sent."
[ "$(container_running "$bot_container")" = true ] \
    || die "The bot isn't running, so nothing would take the request. ./status.sh shows why, and docker logs --tail 50 $bot_container what it last did. Nothing was sent."

# Until the bot has answered: no deploy or setup can recreate a container or take the key away meanwhile, and a restart
# never lands in a deploy's readiness check
lock_is_free || die "Another run holds the lock: $(lock_holder). Wait for it to finish, then run this again. Nothing was sent."
take_lock request 10 "Another run holds the lock: $(lock_holder). Wait for it to finish, then run this again. Nothing was sent."

step="reading dashboard.env"
connection=$(env_value "$home/dashboard.env" ConnectionStrings__BotConnection) \
    || die "dashboard.env's ConnectionStrings__BotConnection can't be used as it is (above). Nothing was sent."
[ -n "$connection" ] \
    || die "dashboard.env has no ConnectionStrings__BotConnection, so there's no database to send the request through. Run ./setup-dashboard.sh --no-questions. Nothing was sent."
key=$(env_value "$home/dashboard.env" Dashboard__RequestSigningKey)
owners=$(env_value "$home/dashboard.env" Dashboard__Owners) \
    || die "dashboard.env's Dashboard__Owners can't be used as it is (above). Nothing was sent."

step="sending the request"
passed=(-e ConnectionStrings__BotConnection -e Dashboard__RequestSigningKey -e Dashboard__Owners)
if [ "$text_given" = yes ]; then
    passed+=(-e MINERBOT_REQUEST_TEXT)
fi
one_off host "$kind" "${options[@]}"

# What became of it, for the history: the state the line that ends a request starts with, and the request's number
number=$(sed -n 's/^Request \([0-9]\{1,20\}\) (.*/\1/p' "$out" | tail -n 1)
word=$(sed -n 's/^\(Done\|Failed\|Refused\|Expired\|Unknown\) at [0-9][0-9]:[0-9][0-9]:[0-9][0-9] UTC: .*/\1/p' "$out" | tail -n 1)
word=${word,,}
note=${number:+request $number}
if too_old; then
    ended=1
    word="not sent"
elif [ "$ended" = 124 ] || [ "$ended" = 137 ]; then
    echo "The request command didn't end within $limit seconds, so it was stopped. The request's row says how it ended: ./status.sh shows whether the bot runs."
    ended=1
    word=${word:-no answer}
elif [ -z "$word" ] && [ -n "$number" ]; then
    word="no answer"
elif [ -z "$word" ]; then
    word="not sent"
    if [ "$ended" != 1 ] && [ "$ended" != 2 ]; then
        echo "Docker couldn't run the request command (above). Nothing was sent."
        ended=1
    fi
elif [ "$word" = "done" ] && [ "$ended" != 0 ]; then
    note+=", the bot wasn't back on Discord"
fi
log_history request "$kind" - "$word" "$note"
exit "$ended"

#!/usr/bin/env bash
# Asks for the bot's secrets and saves them in /opt/minerbot/bot.env: BOTTOKEN, WEBHOOKCHANNEL, STEAMWEB_PUB_KEY and
# STEAMUSER_NAME. What you type isn't shown, Enter keeps the current value, and each question waits at most 10 minutes.
# A value must follow the one safe-character rule: printable ASCII only; none of $, ", ', \ and the backtick; no space
# at the start or the end; no # right after a space; spaces inside a value are fine. Compose and docker run take any
# other value differently.
# Nothing is written until every question has its answer. bot.env is replaced in one step, and every other line keeps
# its place. The bot reads bot.env only when its container is created, so this ends with the command that does that.
# Usage: ./fill-secrets.sh
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

step="checking before any question"
trap 'stopped $? $LINENO' ERR
[ $# -eq 0 ] || die "Usage: ./fill-secrets.sh"
[ "$(id -u)" = 0 ] || die "Run this as root. Nothing was changed."
env=$home/bot.env
[ -f "$env" ] || die "$env is missing, so there's nothing to fill in. Nothing was changed."
# While this runs, no deploy can start, so the bot is never recreated on half the answers
take_lock fill-secrets
trap 'status=$?; if on_terminal; then stty echo 2> /dev/null || true; fi; exit "$status"' EXIT

check_value() {   # an empty answer keeps the current value; anything else must follow the safe-character rule
    answer=$1
    if [ -z "$answer" ] || env_safe "$answer"; then
        return 0
    fi
    echo "That value can't go into bot.env: $env_rule."
    return 1
}

names=(BOTTOKEN WEBHOOKCHANNEL STEAMWEB_PUB_KEY STEAMUSER_NAME)
changed=()
values=()
for name in "${names[@]}"; do
    step="asking for $name"
    ask "$name (it isn't shown; Enter keeps the current value)" "" check_value hidden
    if [ -n "$answer" ]; then
        changed+=("$name")
        values+=("$answer")
    fi
done
answer=

if [ ${#changed[@]} -eq 0 ]; then
    echo "Nothing was changed."
    exit 0
fi
step="writing bot.env"
for i in "${!changed[@]}"; do
    write_env "$env" "${changed[$i]}" "${values[$i]}"
done
values=()
echo "Changed in bot.env: ${changed[*]}."
echo "The bot reads bot.env only when its container is created. To use the new values, in $home run:"
echo "  docker compose up -d --no-build --no-deps --force-recreate bot"

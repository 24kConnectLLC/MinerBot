#!/usr/bin/env bash
# Swaps one service between the image it runs and its :previous image, by image ID: run it again and it goes forward
# again, so both images stay one command away. It never touches the database, the rights, nginx or the other service.
# Migrations only add, and an older image works with a newer database, so going back is safe. dashboard.env stays as it
# is, which is why a setting is never renamed or removed in the pull request that stops using it (deploy/README.md).
# Usage: ./rollback.sh bot|dashboard
# The change runs as the systemd unit minerbot-rollback, for 15 minutes at most, and this terminal follows its log.
# Exit code: 0 done; 1 refused or not ready, with the reason.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

action=rollback
step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: ./rollback.sh bot|dashboard"

[ $# -eq 1 ] || die "$usage"
service=$1
case $service in
    bot | dashboard) ;;
    *) die "$usage" ;;
esac
subject=$service

foreground_part() {
    step="checking before it starts"
    [ "$(id -u)" = 0 ] || die "Run this as root. Nothing was changed."
    cd "$home"
    load_server_conf
    lock_is_free || die "Another run holds the lock: $(lock_holder). Wait for it to finish, then try again. Nothing was changed."
    echo "Rolling the $service back to its previous image."
    if run_detached rollback 900 "$home/rollback.sh" "$service"; then
        exit 0
    fi
    exit 1
}

detached_part() {
    local image=minerbot-$service container cur prev
    exec >> "$MINERBOT_LOG" 2>&1
    run_mode=detached
    step="taking the lock"
    take_lock "rollback $service"
    cd "$home"
    echo "Rollback of the $service, as the unit minerbot-rollback, $(date -u +%Y-%m-%dT%H:%M:%SZ)."

    step="loading server.conf"
    load_server_conf

    step="looking at the images"
    container=$(container_of "$service")
    cur=$(container_image "$container")
    if [ -z "$cur" ]; then
        cur=$(image_id "$image:current")
    fi
    prev=$(image_id "$image:previous")
    if [ -z "$prev" ] || [ "$prev" = "$cur" ]; then
        rev=$(revision "$cur")
        echo "There's nothing to go back to: $image:previous is missing, or it's the image the $service runs. Nothing was changed."
        finish failed "nothing to go back to"
    fi
    rev=$(revision "$prev")

    step="swapping the $service back"
    echo "Swapping the $service from $(revision "$cur") to $rev."
    if [ -n "$cur" ]; then
        tag_id "$cur" "$image:previous"
    fi
    tag_id "$prev" "$image:current"
    start_service "$service" || true
    if ready_service "$service" "" 0; then
        echo "The $service runs $rev and is ready. Run ./rollback.sh $service again to go forward again."
        finish ok "was $(revision "$cur")"
    fi
    echo "The $service isn't ready on $rev either (above), so the problem is probably outside the image: settings, database or server. It's left running on $rev. Run ./rollback.sh $service again to go forward again."
    finish failed "not ready on it either, left running"
}

if [ "${MINERBOT_DETACHED:-}" = 1 ]; then
    [ -n "${MINERBOT_LOG:-}" ] || die "MINERBOT_DETACHED is set without MINERBOT_LOG. Nothing was changed."
    detached_part
else
    foreground_part
fi

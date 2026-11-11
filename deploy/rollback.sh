#!/usr/bin/env bash
# Swaps one service between the image it runs and its :previous image, by image ID: run it again and it goes forward
# again, so both images stay one command away. It never touches the database, the rights, nginx or the other service.
# When the container has another image than :current, a deploy's or rollback's swap was cut off after it moved the tags,
# by a restart of the server or a stop of its unit, or Compose didn't put a deploy's failed swap back: then a run
# finishes that swap on :current first, and moves no tag.
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

# A swap cut off after it moved the tags, before Compose swapped the container: Docker was stopping the old container,
# or hadn't begun, when the server restarted or the unit was stopped. The container still has the image it ran, stopped,
# or running again after a restart, while :current names the image the swap went to. This finishes that swap: the
# service's containers go first, the half-made replacement Compose leaves under a temporary name among them, since a
# plain up could start that one under its temporary name; the volumes stay. Then the service starts on :current, and no
# tag moves, so the next run goes back to :previous as usual. A deploy's failed swap that Compose didn't put back leaves
# the same state, with the container on the new image, and is finished the same way.
finish_cut_off() {
    local was back='' id
    was=$(revision "$cur")
    rev=$(revision "$current")
    id=$(image_id "$image:previous")
    if [ -n "$id" ]; then
        back=$(revision "$image:previous")
    fi
    step="finishing a swap that was cut off"
    echo "The $service's container has $was, but $image:current is $rev: a deploy or rollback was cut off after it moved the tags, before Compose swapped the container, or Compose didn't put a failed swap back. This run finishes that swap on $rev, and moves no tag."
    if ! compose 90 rm -s -f "$service"; then
        echo "Compose couldn't remove the $service's containers (above). No tag was moved. Run ./rollback.sh $service again once Docker answers."
        finish failed "compose failed, finishing a cut-off swap"
    fi
    start_service "$service" || up=failed
    if [ "$up" = ok ] && ready_service "$service" "" 0 "$current"; then
        note_ready "$service"
        echo "The $service runs $rev and is ready: the swap that was cut off is finished.${back:+ Run ./rollback.sh $service again to go back to $back.}"
        finish ok "finished a cut-off swap, was $was"
    fi
    if [ "$up" = failed ]; then
        echo "Compose couldn't start the $service on $rev (above), and its old container is gone, so it doesn't run. No tag was moved. Start it on $rev with: docker compose up -d --no-build --no-deps $service${back:+, or run ./rollback.sh $service again to go back to $back}."
        finish failed "compose failed, finishing a cut-off swap"
    fi
    echo "The $service isn't ready on $rev (above), so the problem is probably outside the image: settings, compose.yml, database or server. It's left on $rev.${back:+ Run ./rollback.sh $service again to go back to $back.}"
    finish failed "not ready, finishing a cut-off swap"
}

detached_part() {
    local image=minerbot-$service container cur current prev up=ok runs
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
    current=$(image_id "$image:current")
    if [ -n "$cur" ] && [ -n "$current" ] && [ "$cur" != "$current" ]; then
        finish_cut_off
    fi
    if [ -z "$cur" ]; then
        cur=$current
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
    # A run cut off here leaves the tags moved and the container on the image before: the next run finishes the swap
    cut_point tags
    start_service "$service" || up=failed
    if [ "$up" = ok ] && ready_service "$service" "" 0 "$prev"; then
        note_ready "$service"
        echo "The $service runs $rev and is ready. Run ./rollback.sh $service again to go forward again."
        finish ok "was $(revision "$cur")"
    fi
    runs=$(container_image "$container")
    if [ -n "$cur" ] && [ "$runs" = "$cur" ]; then
        # Compose never replaced the container, so nothing changed, and the tags go back too
        tag_id "$cur" "$image:current"
        tag_id "$prev" "$image:previous"
        echo "Compose didn't recreate the $service on $rev (above), so it still runs $(revision "$cur"), and its tags are back as they were. Nothing was changed."
        finish failed "compose didn't recreate it, still on $(revision "$cur")"
    fi
    if [ "$up" = failed ]; then
        echo "Compose failed while it recreated the $service on $rev (above). Its tags now name $rev. Run ./rollback.sh $service again to go forward again."
        finish failed "compose failed"
    fi
    echo "The $service isn't ready on $rev either (above), so the problem is probably outside the image: settings, compose.yml, database or server. It's left on $rev. Run ./rollback.sh $service again to go forward again."
    finish failed "not ready on it either, left running"
}

if [ "${MINERBOT_DETACHED:-}" = 1 ]; then
    [ -n "${MINERBOT_LOG:-}" ] || die "MINERBOT_DETACHED is set without MINERBOT_LOG. Nothing was changed."
    detached_part
else
    foreground_part
fi

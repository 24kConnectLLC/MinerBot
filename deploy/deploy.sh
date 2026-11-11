#!/usr/bin/env bash
# Builds a commit of MinerBot from GitHub and swaps it in, the dashboard first, then the bot. Before any swap it installs
# the commit's deploy files, dumps and migrates MinerBot's database when the commit has new migrations, checks the
# dashboard's settings with its own validator, and applies the dashboard's database rights: the installed file, or for a
# commit older than the installed deploy files, the rights the last apply used and the GRANTs of its own
# dashboard-rights.sql that they lack; and the GRANTs that the dashboard that runs, :current and :previous have in their
# own dashboard-rights.sql, so every dashboard the swap or rollback.sh can put back keeps what it uses, unless its commit
# is unknown here, which the log warns of. A service that isn't ready after its swap goes back to the exact image it ran,
# under the compose.yml it was last ready under, and the deploy stops there; when Compose doesn't put it back, the
# deploy says what runs instead and what to do.
# Usage: ./deploy.sh [branch, tag or commit] [--only bot|dashboard] [--no-start]   (main and both services by default)
#   --only bot|dashboard  swaps only that one. The dashboard's image is still built when migrations are due: it runs them.
#   --no-start            stops after the build and the migrations, and swaps nothing.
# Only commits main itself pointed at on GitHub are deployed: the ones git log --first-parent refs/remotes/origin/main
# lists in src. A commit inside a merged pull request never is. The part that changes things runs as the systemd unit
# minerbot-deploy, for 90 minutes at most, and this terminal follows its log; closing the terminal doesn't stop it.
# Exit code: 0 done; 1 refused or stopped, with the reason.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

action=deploy
step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: ./deploy.sh [branch, tag or commit] [--only bot|dashboard] [--no-start]"

usage_error() {   # usage_error [<reason>]
    die "${1:+$1 }$usage"
}

ref=main
ref_given=no
only=
start=yes
args=("$@")
while [ $# -gt 0 ]; do
    case $1 in
        --only)
            if [ -n "$only" ] || [ $# -lt 2 ]; then
                usage_error
            fi
            case $2 in
                bot | dashboard) only=$2 ;;
                *) usage_error "--only takes bot or dashboard." ;;
            esac
            shift
            ;;
        --no-start)
            [ "$start" = yes ] || usage_error
            start=no
            ;;
        -*)
            usage_error "Unknown option $1."
            ;;
        *)
            [ "$ref_given" = no ] || usage_error "Give one branch, tag or commit."
            if ! [[ $1 =~ ^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$ ]] || [[ $1 == *..* ]]; then
                usage_error "That isn't a branch, tag or commit name."
            fi
            ref=$1
            ref_given=yes
            ;;
    esac
    shift
done
if [ -n "$only" ]; then
    services=("$only")
else
    services=(dashboard bot)
fi
subject=$(IFS=,; echo "${services[*]}")
start_note=
if [ "$start" = no ]; then
    start_note=", build only"
fi

selected() {   # selected bot|dashboard: true when this deploy swaps that service
    [[ " ${services[*]} " == *" $1 "* ]]
}

# ---- The foreground part: it changes nothing, and runs where it was typed

foreground_part() {
    step="checking before it starts"
    [ "$(id -u)" = 0 ] || die "Run this as root. Nothing was changed."
    cd "$home"
    load_server_conf
    if [ "$start" = yes ] && selected dashboard && [ ! -f "$home/dashboard.env" ]; then
        die "There's no dashboard.env yet. Build with --only dashboard --no-start, run ./setup-dashboard.sh, then deploy. To deploy only the bot meanwhile: --only bot. Nothing was changed."
    fi
    lock_is_free || die "Another run holds the lock: $(lock_holder). Wait for it to finish, then deploy again. Nothing was changed."
    echo "Deploying $ref: ${subject/,/ and }$start_note."
    if run_detached deploy 5400 "$home/deploy.sh" "${args[@]}"; then
        exit 0
    fi
    exit 1
}

# ---- The detached part: the unit minerbot-deploy, which writes only to its log

# shellcheck disable=SC2317,SC2329 # the EXIT trap runs it
cleanup() {
    if [ -n "${deploy_stage:-}" ]; then
        rm -rf "$deploy_stage"
    fi
    if [ -n "${rights_dir:-}" ]; then
        rm -rf "$rights_dir"
    fi
    if [ "${rights_check_made:-}" = yes ]; then
        drop_rights_check
    fi
}

# Warns, or refuses when there isn't room for a build: 5 GiB free on /srv, where Docker keeps its images, and 2 GiB on /
preflight() {
    local avail problem
    run_for 20 docker info > /dev/null || die "Docker doesn't answer (above). Nothing was changed."
    avail=$(free_kib /srv) || die "Couldn't tell how much space is free on /srv. Nothing was changed."
    [ "$avail" -ge 5242880 ] \
        || die "Less than 5 GiB is free on /srv, where Docker keeps its images and builds. Make room first: README.txt says how. Nothing was changed."
    avail=$(free_kib /) || die "Couldn't tell how much space is free on /. Nothing was changed."
    [ "$avail" -ge 2097152 ] || die "Less than 2 GiB is free on /. Make room first: README.txt says how. Nothing was changed."
    if [ -e "$home/state/nginx-change" ]; then
        echo "Warning: an nginx change was cut off. Run ./setup-dashboard.sh, which undoes it first."
    fi
    while IFS= read -r -t 5 problem; do
        if [ -n "$problem" ]; then
            echo "Warning: $problem"
        fi
    done < <(backup_problems)
}

# The commit to deploy. Only from GitHub's MinerBot, as src's git config writes the address, since remote get-url would
# follow an insteadOf rule. The fetch takes GitHub's tags as they are now, moved and deleted ones too. The ref is looked
# up by its full name, a branch on GitHub first, then a tag, then a commit ID, so a tag can never stand in for a branch,
# and the commit must be one main itself pointed at: one on its first-parent line, which a commit inside a merged pull
# request never is. A run started again after installing new deploy files fetches nothing and builds exactly the commit
# whose files it installed.
choose_commit() {
    local url head chain
    url=$(run_for 5 git -C "$home/src" config --get remote.origin.url) || url=
    case $url in
        "$repository_url" | "$repository_url.git") ;;
        *) die "src's origin is ${url:-not set} in its git config, not $repository_url, and deploy.sh builds only from there. Nothing was changed." ;;
    esac
    if [ -n "${MINERBOT_REEXEC:-}" ]; then
        commit=$MINERBOT_REEXEC
        [[ $commit =~ ^[0-9a-f]{40}$ ]] || die "MINERBOT_REEXEC must be the full commit whose deploy files this run installed. Nothing more was changed."
        head=$(run_for 10 git -C "$home/src" rev-parse HEAD) || die "git couldn't tell which commit src is at (above). Nothing more was changed."
        [ "$head" = "$commit" ] \
            || die "This deploy ran again for ${commit:0:12}, but src is at ${head:0:12}. Nothing more was changed: deploy again."
    else
        run_for 120 git -C "$home/src" fetch --prune --prune-tags --force --tags origin \
            || die "Couldn't fetch from GitHub within 2 minutes; git's reason is above. Nothing was changed."
        commit=$(run_for 10 git -C "$home/src" rev-parse --verify --quiet "refs/remotes/origin/$ref^{commit}") \
            || commit=$(run_for 10 git -C "$home/src" rev-parse --verify --quiet "refs/tags/$ref^{commit}") \
            || { [[ $ref =~ ^[0-9a-fA-F]{4,40}$ ]] \
                && commit=$(run_for 10 git -C "$home/src" rev-parse --verify --quiet "$ref^{commit}") \
                && [[ $commit == "${ref,,}"* ]]; } \
            || die "There's no branch, tag or commit called $ref on GitHub. Nothing was changed."
        chain=$(run_for 30 git -C "$home/src" rev-list --first-parent refs/remotes/origin/main) \
            || die "git couldn't list main's commits (above). Nothing was changed."
        grep -qxF -- "$commit" <<< "$chain" \
            || die "${commit:0:12} ($ref) isn't one of main's own commits: it isn't merged into main, or it's inside a merged pull request, where it was never reviewed on its own. deploy.sh deploys only commits main itself pointed at, because root's scripts come from them. git -C $home/src log --first-parent --oneline refs/remotes/origin/main lists them. Nothing was changed."
    fi
    run_for 60 git -C "$home/src" checkout --quiet --force --detach "$commit" \
        || die "Couldn't check out ${commit:0:12} in src (above). Nothing was swapped."
    run_for 60 git -C "$home/src" clean -ffdxq || die "Couldn't clean src (above). Nothing was swapped."
    [ -d "$home/src/deploy" ] \
        || die "${commit:0:12} has no deploy/ folder, so it's older than these scripts and they can't deploy it. To go back to an earlier image, use ./rollback.sh. Nothing was swapped."
    tag12=${commit:0:12}
    rev=$tag12
    echo "The commit is $tag12 ($ref)."
}

build() {   # build bot|dashboard: minerbot-<service>:new from src, on two CPUs, labelled with its commit
    local dockerfile=MinerBot.Dashboard/Dockerfile
    if [ "$1" = bot ]; then
        dockerfile="MinerBot 2.0/Dockerfile"
    fi
    echo "Building the $1 at $tag12 on two CPUs."
    run_for -k 30 1200 docker build --pull --network host --cpu-quota 200000 --cpu-period 100000 \
        --label "org.opencontainers.image.revision=$commit" -f "$home/src/$dockerfile" -t "minerbot-$1:new" "$home/src" \
        || die "Building the $1 failed or took more than 20 minutes (above). Nothing was swapped; what runs is unchanged."
    echo "Built minerbot-$1:new."
}

# Docker's restart count for the container, or 0 when there's no such container. Any other failure stops the deploy, as
# in container_image: a 0 that only meant no answer would make a restart from before look like one during the deploy.
restart_count() {   # restart_count <container>
    local out status=0
    out=$(run_for 30 docker container inspect -f '{{.RestartCount}}' "$1" 2>&1) || status=$?
    if [ "$status" = 0 ] && [[ $out =~ ^[0-9]+$ ]]; then
        printf '%s\n' "$out"
        return 0
    fi
    case $out in
        *"No such container"* | *"No such object"*)
            echo 0
            return 0
            ;;
    esac
    printf '%s\n' "$out" >&2
    die "Docker didn't answer about the container $1 (above). Nothing more was changed."
}

# The container's last 200 log lines, into logs/failed-<service>-<UTC>.log, which only root can open. Never on the
# screen: the bot's log can hold a Steam sign-in code.
save_failed_log() {   # save_failed_log bot|dashboard
    failed_log=$home/logs/failed-$1-$(date -u +%Y%m%dT%H%M%SZ).log
    run_for 30 docker logs --tail 200 "$(container_of "$1")" > "$failed_log" 2>&1 || true
    chmod 600 "$failed_log"
}

# After a failed swap: when compose.yml changed the service's settings since it was last ready (compose_changed), the
# compose.yml it was ready under goes back in place, a copy beside it renamed over it, so the image it goes back to runs
# as it did. The next deploy installs the new one again. Sets compose_note to the sentences for the messages, or nothing.
put_back_compose() {   # put_back_compose bot|dashboard
    local service=$1 other=bot kept=$home/state/compose-ready-$1.yml tmp now was
    compose_note=
    compose_changed "$service" || return 0
    if [ "$service" = bot ]; then
        other=dashboard
    fi
    now=$(run_for 30 docker compose -f "$home/compose.yml" --project-directory "$home" config --hash "$other" 2> /dev/null) || now=
    was=$(run_for 30 docker compose -f "$kept" --project-directory "$home" config --hash "$other" 2> /dev/null) || was=
    tmp=$(mktemp "$home/.minerbot-install.XXXXXX")
    cp "$kept" "$tmp"
    chmod 600 "$tmp"
    mv -f "$tmp" "$home/compose.yml"
    echo "compose.yml changed the $service's settings since it was last ready, so the compose.yml it was ready under is back in place."
    compose_note=" compose.yml changed the $service's settings since it was last ready, so the compose.yml it was ready under is back in place, until the next deploy installs the new one again."
    if [ "$now" != "$was" ]; then
        compose_note+=" That one gives the $other other settings too: its container keeps the ones it started with, until it's recreated."
    fi
}

# Swaps a service onto minerbot-<service>:new by image ID. :previous moves only when the image really changes. When
# Compose fails, or the container doesn't then run the new image, or it isn't ready, the service goes back to the exact
# image it ran, under the compose.yml it was last ready under (put_back_compose), the tags go back to what they were (the
# new image is tagged :failed), and the deploy stops. When Compose doesn't put it back, the deploy says what runs: the
# new image, which rollback.sh then puts back, or nothing. A service that is ready keeps a copy of the compose.yml it's
# ready under (note_ready).
swap() {   # swap bot|dashboard
    local service=$1 image=minerbot-$1 container new before prev public_before='' base up=ok what word runs
    container=$(container_of "$service")
    new=$(image_id "$image:new")
    [ -n "$new" ] || die "There's no $image:new to swap in. Nothing was swapped."
    before=$(container_image "$container")
    if [ -z "$before" ]; then
        before=$(image_id "$image:current")
    fi
    prev=$(image_id "$image:previous")

    if [ "$new" = "$before" ]; then
        echo "The $service is already on $(revision "$new"). Making sure it runs and is ready."
        start_service "$service" || up=failed
        # Counted once Compose is done: a container it started or recreated begins again at 0, and one it left running
        # keeps its earlier restarts, which aren't this deploy's
        base=$(restart_count "$container")
        if [ "$up" = ok ] && ready_service "$service" "" "$base" "$new"; then
            note_ready "$service"
            log_history deploy "$service" "$(revision "$new")" ok "already on it"
            return 0
        fi
        what="The $service is already on $(revision "$new"), but it isn't ready (above)"
        if [ "$up" = failed ]; then
            what="The $service is already on $(revision "$new"), but Compose couldn't start or recreate it (above)"
        fi
        save_failed_log "$service"
        put_back_compose "$service"
        if [ -n "$compose_note" ]; then
            start_service "$service" || true
            base=$(restart_count "$container")
            if ready_service "$service" "" "$base" "$new"; then
                note_ready "$service"
                log_history deploy "$service" "$(revision "$new")" "not ready" "already on it, ready again under the earlier compose.yml"
                die "$what.$compose_note It's ready again with that one. No tag was moved, and the deploy stopped here. Its last log lines are in ${failed_log#"$home"/}."
            fi
        fi
        log_history deploy "$service" "$(revision "$new")" "not ready" "already on it"
        die "$what.$compose_note No tag was moved. Its last log lines are in ${failed_log#"$home"/}."
    fi

    if [ -n "$before" ]; then
        echo "Swapping the $service from $(revision "$before") to $(revision "$new")."
    else
        echo "Starting the $service on $(revision "$new"). It had no image before."
    fi
    if [ -n "$before" ]; then
        tag_id "$before" "$image:previous"
    fi
    tag_id "$new" "$image:current"
    if [ "$service" = dashboard ]; then
        public_before=$(public_status)
        echo "The public home page answers $public_before before the swap."
    fi
    # A run cut off here leaves the tags moved and the container on the image before: rollback.sh finishes such a swap
    cut_point "tags-$service"
    start_service "$service" || up=failed
    if [ "$up" = ok ] && ready_service "$service" "$public_before" 0 "$new"; then
        note_ready "$service"
        log_history deploy "$service" "$(revision "$new")" ok "was $(revision "$before")"
        return 0
    fi

    what="The $service on $(revision "$new") wasn't ready"
    word="not ready"
    if [ "$up" = failed ]; then
        what="Compose couldn't recreate the $service on $(revision "$new") (above)"
        word="compose failed"
    fi
    save_failed_log "$service"
    tag_id "$new" "$image:failed"
    if [ -z "$before" ]; then
        compose 60 stop "$service" || true
        compose 30 rm -f "$service" || true
        untag "$image:current"
        # Compose's rm -f removes only a stopped container, so a stop that failed leaves it running. The assignment comes
        # first, so Docker not answering stops the deploy here.
        runs=$(container_image "$container")
        if [ -n "$runs" ]; then
            log_history deploy "$service" "$(revision "$new")" "$word" "not stopped, with no earlier image"
            die "$what, and there's no earlier image to go back to, but Compose couldn't stop and remove it (above), so it's left on $(revision "$new"). Stop and remove it with: docker compose rm -s -f $service. Its container's last log lines are in ${failed_log#"$home"/}, and the image is tagged $image:failed."
        fi
        log_history deploy "$service" "$(revision "$new")" "$word" "stopped, with no earlier image"
        die "$what, and there's no earlier image to go back to, so it's stopped and removed. Its container's last log lines are in ${failed_log#"$home"/}, and the image is tagged $image:failed."
    fi
    tag_id "$before" "$image:current"
    if [ -n "$prev" ]; then
        tag_id "$prev" "$image:previous"
    else
        untag "$image:previous"
    fi
    put_back_compose "$service"
    echo "Putting the $service back on $(revision "$before")."
    start_service "$service" || true
    # When Compose failed, or returned without recreating it, the container doesn't run the image before, and its
    # readiness would say nothing about that image. It still runs the new one, which rollback.sh takes for a swap cut
    # off after the tags moved and puts back on :current, or it's gone.
    runs=$(container_image "$container")
    if [ "$runs" != "$before" ]; then
        if [ -n "$runs" ]; then
            log_history deploy "$service" "$(revision "$new")" "$word" "not put back on $(revision "$before"), left on $(revision "$runs")"
            die "$what, and Compose didn't put it back on $(revision "$before") (above), so it's left on $(revision "$runs"), while $image:current names $(revision "$before").$compose_note Run ./rollback.sh $service once Docker answers: it finishes the swap back. Its container's last log lines are in ${failed_log#"$home"/}, and $(revision "$new") is tagged $image:failed."
        fi
        log_history deploy "$service" "$(revision "$new")" "$word" "not put back on $(revision "$before"), not running"
        die "$what, and Compose didn't put it back on $(revision "$before") (above), and its container is gone, so it doesn't run.$compose_note Start it on $(revision "$before"), which $image:current names, with: docker compose up -d --no-build --no-deps $service. Its container's last log lines are in ${failed_log#"$home"/}, and $(revision "$new") is tagged $image:failed."
    fi
    base=$(restart_count "$container")
    if ready_service "$service" "" "$base" "$before"; then
        note_ready "$service"
        log_history deploy "$service" "$(revision "$new")" "$word" "back on $(revision "$before")${compose_note:+, under the earlier compose.yml}"
        die "$what, so it's back on $(revision "$before"), which is ready.$compose_note The deploy stopped here. Its container's last log lines are in ${failed_log#"$home"/}, and $(revision "$new") is tagged $image:failed."
    fi
    log_history deploy "$service" "$(revision "$new")" "$word" "nor on $(revision "$before")"
    die "$what, and it isn't ready on the image before it, $(revision "$before"), either, so the problem is outside the image: settings, compose.yml, database or server.$compose_note It's left on $(revision "$before"). Logs: ${failed_log#"$home"/}, and: docker logs --tail 50 $container"
}

prune_run_logs() {   # keeps the newest 60 run logs
    local -a logs=("$home"/logs/[0-9]*Z-*.log)
    [ -e "${logs[0]}" ] || return 0
    if [ ${#logs[@]} -gt 60 ]; then
        rm -f -- "${logs[@]:0:${#logs[@]}-60}"
    fi
}

nginx_note() {
    if nginx_file_differs; then
        echo "The nginx file changed. Run ./setup-dashboard.sh --no-questions to install it: it says what it will change, asks, and tests the file first."
    fi
}

# The statements of a rights file on stdin, one per line, as MariaDB runs them: blank lines and lines that start with --
# are left out, a statement's lines are joined, and each run of spaces becomes one. A semicolon at the end of a line
# ends a statement, as the file's own rules say.
rights_statements() {
    awk '
        { sub(/\r$/, "") }
        /^[ \t]*--/ || /^[ \t]*$/ { next }
        { gsub(/[ \t]+/, " "); sub(/^ /, ""); sub(/ $/, "")
          statement = (statement == "") ? $0 : (statement " " $0)
          if ($0 ~ /;$/) { print statement; statement = "" } }
        END { if (statement != "") print statement }'
}

# The full commit a dashboard image was built from, by its revision label, or nothing when there's no such image or it
# has no such label. Docker not answering stops the deploy, before any right is applied.
image_commit() {   # image_commit <image or ID>
    local out status=0 label=''
    out=$(run_for 30 docker image inspect \
        -f '{{.Id}} {{with index .Config "Labels"}}{{with index . "org.opencontainers.image.revision"}}{{.}}{{end}}{{end}}' \
        "$1" 2>&1) || status=$?
    if [ "$status" != 0 ]; then
        case $out in
            *"No such image"* | *"No such object"*) return 0 ;;
        esac
        printf '%s\n' "$out" >&2
        die "Docker didn't answer about the image $1 (above). Nothing was swapped."
    fi
    read -r -t 5 _ label <<< "$out" || true
    if [[ $label =~ ^[0-9a-f]{40}$ ]]; then
        printf '%s\n' "$label"
    fi
}

# Adds GRANT statements to the rights being built. The first time, rights_file is copied into a folder of its own in
# state/tmp as dashboard-rights.with-<12>.sql, which rights_file then names; each later time, the name gets one more
# <12>.
add_grants() {   # add_grants <12 characters of the commit> <statements> <the end of the comment line>
    local next
    if [ -z "${rights_dir:-}" ]; then
        install -d -m 700 "$home/state" "$home/state/tmp"
        rights_dir=$(mktemp -d "$home/state/tmp/deploy.rights.XXXXXX")
        next=$rights_dir/dashboard-rights.with-$1.sql
        cp "$rights_file" "$next"
    else
        next=${rights_file%.sql}-$1.sql
        mv -f "$rights_file" "$next"
    fi
    {
        printf '\n%s\n' "-- Added by deploy.sh: the GRANT statements of $1's own dashboard-rights.sql that the rights above lack$3"
        printf '%s\n' "$2"
    } >> "$next"
    rights_file=$next
}

# The rights the dashboard's swap applies. Sets rights_file, the file to apply: the installed dashboard-rights.sql, with
# these GRANT statements added, each named in the log, so every dashboard image the swap or rollback.sh can run before
# the next apply keeps what it uses. The exceptions: an image whose commit src lacks, which the log warns of, and one
# without a commit label, which no image deploy.sh builds is.
# - For a dashboard older than the installed deploy files, which install_deploy_files kept, a pull request after its
#   commit may have removed a right it still uses, as the rule for removing one allows. Its base is the rights the last
#   apply used, which the dashboard that runs now has (state/dashboard-rights.applied.sql, or the installed file before
#   any apply kept a copy), and it gets every GRANT statement of its own dashboard-rights.sql they lack.
# - The dashboard that runs now, which a failed swap goes back to and which becomes :previous after one that works, and
#   :current and :previous, where rollback.sh goes, keep every GRANT statement of their own dashboard-rights.sql that
#   the rights lack. So a right a pull request removed goes only once no such dashboard has it in its own file, a deploy
#   or two after that pull request's.
# apply_rights keeps the result as the copy setup applies.
swap_rights() {
    local installed='' base what own missing lack c ran current previous seen='' status
    rights_file=$home/dashboard-rights.sql
    lack="the installed dashboard-rights.sql lacks"
    if [ -f "$home/state/deploy-files.commit" ]; then
        IFS= read -r -t 5 installed < "$home/state/deploy-files.commit" || true
    fi
    if [ -n "$installed" ] && [ "$installed" != "$commit" ]; then
        base=$home/state/dashboard-rights.applied.sql
        what="the rights the last apply used, from state/dashboard-rights.applied.sql"
        if [ ! -f "$base" ]; then
            base=$home/dashboard-rights.sql
            what="the installed dashboard-rights.sql, since no apply has kept a copy yet"
        fi
        rights_file=$base
        own=$(run_for 30 git -C "$home/src" show "$commit:deploy/dashboard-rights.sql") \
            || die "git couldn't read the dashboard-rights.sql of $tag12 (above). Nothing was swapped."
        missing=$(rights_statements <<< "$own" | grep -E '^GRANT ' | grep -vxF -f <(rights_statements < "$base")) \
            || missing=
        if [ -z "$missing" ]; then
            echo "The deploy files stay those of ${installed:0:12} (above), so the dashboard of $tag12 gets $what. Its own dashboard-rights.sql gives no right they lack."
        else
            add_grants "$tag12" "$missing" ""
            echo "The deploy files stay those of ${installed:0:12} (above), so the dashboard of $tag12 gets $what, and these GRANT statements of its own dashboard-rights.sql, which they lack and its code may still use:"
            printf '%s\n' "$missing" | sed 's/^/  /'
        fi
        lack="the rights above lack"
    fi
    current=$(image_commit minerbot-dashboard:current)
    previous=$(image_commit minerbot-dashboard:previous)
    ran=$(container_image "$dashboard_container")
    if [ -n "$ran" ]; then
        ran=$(image_commit "$ran")
    fi
    for c in "$ran" "$current" "$previous"; do
        if [ -z "$c" ] || [ "$c" = "$commit" ] || [[ $seen == *" $c"* ]]; then
            continue
        fi
        seen+=" $c"
        status=0
        run_for 10 git -C "$home/src" rev-parse --verify --quiet "$c^{commit}" > /dev/null || status=$?
        if [ "$status" = 1 ]; then
            echo "Warning: src doesn't have ${c:0:12}, which a dashboard image here was built from, so its dashboard-rights.sql can't be read. If a failed swap or ./rollback.sh dashboard puts that dashboard back, it may lack a right its code uses."
            continue
        elif [ "$status" != 0 ]; then
            die "git couldn't tell whether src has ${c:0:12}, which a dashboard image here was built from (above). Nothing was swapped."
        fi
        own=$(run_for 30 git -C "$home/src" show "$c:deploy/dashboard-rights.sql") \
            || die "git couldn't read the dashboard-rights.sql of ${c:0:12} (above). Nothing was swapped."
        missing=$(rights_statements <<< "$own" | grep -E '^GRANT ' \
            | grep -vxF -f <(rights_statements < "$rights_file")) || missing=
        [ -n "$missing" ] || continue
        add_grants "${c:0:12}" "$missing" ", for its dashboard, which a failed swap or rollback.sh can put back"
        echo "The dashboard of ${c:0:12}, which a failed swap or ./rollback.sh dashboard can put back, keeps these GRANT statements of its own dashboard-rights.sql, which $lack:"
        printf '%s\n' "$missing" | sed 's/^/  /'
        lack="the rights above lack"
    done
}

detached_part() {
    local due hosts rights_file
    local -a built=()
    exec >> "$MINERBOT_LOG" 2>&1
    run_mode=detached
    step="taking the lock"
    if [ -n "${MINERBOT_REEXEC:-}" ] && { true >&9; } 2> /dev/null; then
        # The run before this one took the lock, and fd 9 kept it across exec
        flock -w 10 9 || die "This deploy lost its lock when it ran again. Nothing more was changed."
    else
        take_lock deploy
    fi
    trap 'status=$?; cleanup || true; exit "$status"' EXIT
    cd "$home"
    rm -rf "$home"/state/tmp/deploy.*
    echo "Deploy of $ref for ${subject/,/ and }$start_note, as the unit minerbot-deploy, $(date -u +%Y-%m-%dT%H:%M:%SZ), with $(time_left) seconds left.${MINERBOT_REEXEC:+ This run goes on with the new deploy files.}"

    step="loading server.conf"
    load_server_conf

    step="the preflight"
    preflight

    step="choosing the commit"
    choose_commit

    step="installing the deploy files"
    install_deploy_files "$commit"
    if [ "$reexec_due" = yes ] && [ -n "${MINERBOT_REEXEC:-}" ]; then
        echo "deploy.sh or common.sh changed, but this run was started again once already, so it goes on as it is."
    elif [ "$reexec_due" = yes ]; then
        echo "deploy.sh or common.sh changed in $tag12, and the new ones are installed. The deploy goes on with them."
        if [ -n "${deploy_stage:-}" ]; then
            rm -rf "$deploy_stage"
        fi
        exec env MINERBOT_REEXEC="$commit" /bin/bash "$home/deploy.sh" "${args[@]}"
    fi
    if [ -n "${deploy_stage:-}" ]; then
        rm -rf "$deploy_stage"
        deploy_stage=
    fi

    step="checking for migrations"
    due=$(migrations_due "$commit")

    if selected dashboard || [ "$due" = yes ]; then
        step="building the dashboard"
        build dashboard
        built+=(dashboard)
    fi
    if selected bot; then
        step="building the bot"
        build bot
        built+=(bot)
    fi

    if [ "$due" = yes ]; then
        step="dumping the database before the migrations"
        echo "$tag12 has migrations MinerBot's database doesn't have yet, so it's dumped first."
        # 660 seconds, so backup.sh's own limits (the backup lock's 300 and the pre-deploy dump's 300) end first and it
        # reports which step failed
        run_for 660 "$home/backup.sh" pre-deploy \
            || die "The dump before the migrations failed (above), so nothing was migrated and nothing was swapped. What runs is unchanged."
        echo "Dumped."
        step="migrating MinerBot's database"
        run_migrate
        echo "Migrated."
    else
        echo "No migrations are due."
    fi

    if [ "$start" = no ]; then
        nginx_note
        echo "Built ${built[*]} at $tag12. Nothing was swapped (--no-start)."
        finish built
    fi

    if selected dashboard; then
        step="checking the dashboard's settings"
        check_settings "$home/dashboard.env" minerbot-dashboard:new \
            || die "The new dashboard needs settings dashboard.env doesn't have (above). Run ./setup-dashboard.sh, then deploy again. Nothing was swapped."
        step="applying the dashboard's database rights"
        hosts=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM mysql.user WHERE User = '$dashboard_login' AND Host IN ('localhost', '127.0.0.1')") \
            || die "MariaDB didn't answer (above). Nothing was swapped."
        [ "$hosts" = 2 ] \
            || die "The dashboard's database login doesn't exist yet. Run ./setup-dashboard.sh, which creates it, then deploy again. Nothing was swapped."
        swap_rights
        apply_rights "$rights_file" || die "The dashboard's database rights didn't apply (above). Nothing was swapped."

        step="swapping the dashboard"
        [ "$(time_left)" -ge 600 ] \
            || die "There isn't enough time left in this run for a safe swap of the dashboard. Nothing was swapped. Deploy again; the build is cached."
        swap dashboard
    fi

    if selected bot; then
        step="swapping the bot"
        if [ "$(time_left)" -lt 700 ]; then
            if selected dashboard; then
                die "There isn't enough time left in this run for a safe swap of the bot. The dashboard was swapped, the bot wasn't. Deploy again with --only bot; the build is cached."
            fi
            die "There isn't enough time left in this run for a safe swap of the bot. Nothing was swapped. Deploy again; the build is cached."
        fi
        swap bot
    fi

    step="tidying up"
    if [ "$(time_left)" -ge 360 ]; then
        run_for 120 docker image prune -f > /dev/null || echo "Warning: removing dangling images failed (above). Nothing depends on it."
        run_for 180 docker builder prune -f --filter until=168h > /dev/null \
            || echo "Warning: pruning the build cache failed (above). Nothing depends on it."
        prune_run_logs
    else
        echo "The tidy-up was skipped: there wasn't enough time left in this run. Nothing depends on it."
    fi
    nginx_note
    echo "Deployed the ${subject/,/ and } at $tag12."
    finish ok
}

if [ "${MINERBOT_DETACHED:-}" = 1 ]; then
    [ -n "${MINERBOT_LOG:-}" ] || die "MINERBOT_DETACHED is set without MINERBOT_LOG. Nothing was changed."
    detached_part
else
    foreground_part
fi

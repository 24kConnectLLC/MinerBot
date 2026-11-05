#!/usr/bin/env bash
# A look at MinerBot on this server. It only looks: it takes no lock and changes nothing, nginx included, and every probe
# has a limit of 30 seconds or less.
# Usage:
#   ./status.sh               the containers, images, recent deploys, a running action, backups, the certificate,
#                             nginx, the disk and the move's leftovers
#   ./status.sh --check       the nightly check from /etc/cron.d/minerbot: one line per problem and exit 1, so cron mails
#                             it; nothing and exit 0 when all is well
#   ./status.sh --mail-test   sends one message to root, to prove that cron's mail about failures reaches you
#   ./status.sh --nginx-look  nginx's http-level lines, its main context and its build flags, never a secret: the way to
#                             look at nginx's whole configuration, instead of raw nginx -T, which prints secrets
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: ./status.sh [--check | --mail-test | --nginx-look]"
form=show
if [ $# -gt 1 ]; then
    die "$usage"
elif [ $# -eq 1 ]; then
    case $1 in
        --check | --mail-test | --nginx-look) form=${1#--} ;;
        *) die "$usage" ;;
    esac
fi

gib() {   # gib <KiB>: whole GiB
    echo $(($1 / 1048576))
}

# ---- The look

show_container() {   # show_container bot|dashboard
    local name state status health restarts started image
    name=$(container_of "$1")
    state=$(run_for 30 docker container inspect \
        -f '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}no health check{{end}}|{{.RestartCount}}|{{.State.StartedAt}}|{{.Image}}' \
        "$name" 2> /dev/null) || state=
    if [ -z "$state" ]; then
        printf '  %-10s no container\n' "$1"
        return 0
    fi
    IFS='|' read -r -t 5 status health restarts started image <<< "$state" || true
    printf '  %-10s %s, %s, %s restarts, started %sZ, on %s\n' "$1" "$status" "$health" "$restarts" "${started%%.*}" \
        "$(revision "$image")"
}

show_images() {   # show_images bot|dashboard
    local tag line=
    for tag in current previous new failed; do
        line+="$tag $(revision "minerbot-$1:$tag"), "
    done
    printf '  %-10s %s\n' "$1" "${line%, }"
}

show() {
    local service newest hours ends days out n avail commit user group old
    echo "MinerBot on this server, $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo
    echo "Containers"
    for service in bot dashboard; do
        show_container "$service"
    done
    echo "Images"
    for service in bot dashboard; do
        show_images "$service"
    done
    echo
    echo "The last 10 deploys and changes (logs/history.log)"
    if [ -f "$home/logs/history.log" ]; then
        tail -n 10 "$home/logs/history.log" | sed 's/^/  /'
    else
        echo "  none yet"
    fi
    echo
    if lock_is_free; then
        echo "Running now: nothing"
    else
        echo "Running now: $(lock_holder)"
    fi

    newest=$(newest_backup)
    if [ -n "$newest" ]; then
        hours=$((($(date +%s) - $(stat -c %Y "$newest")) / 3600))
        echo "Backups: the newest is ${newest##*/}, $hours hours old"
    else
        echo "Backups: none yet"
    fi
    if [ -f "$backups/backup.log" ]; then
        echo "  backup.log's last line: $(tail -n 1 "$backups/backup.log")"
    fi

    if [ -f "$nginx_secrets/origin-cert.pem" ]; then
        ends=$(cert_end "$nginx_secrets/origin-cert.pem") || ends="unknown, since openssl couldn't parse it"
        days=$(cert_days_left "$nginx_secrets/origin-cert.pem") || days="?"
        echo "Origin certificate: ends $ends, $days days left"
    else
        echo "Origin certificate: not installed yet"
    fi

    if out=$(run_for 30 nginx -t -q 2>&1); then
        echo "nginx -t: passes"
    else
        echo "nginx -t: fails"
        printf '%s\n' "$out" | sed 's/^/  /'
    fi
    if [ -f "$cloudflare_list" ]; then
        n=$(grep -c '^[[:space:]]*set_real_ip_from[[:space:]]' "$cloudflare_list") || n=0
        echo "cPanel's Cloudflare list: $n set_real_ip_from lines"
    else
        echo "cPanel's Cloudflare list: missing ($cloudflare_list)"
    fi
    if [ -e "$home/state/nginx-change" ]; then
        echo "nginx change marker: there, so an nginx change was cut off. Run ./setup-dashboard.sh, which undoes it first."
    else
        echo "nginx change marker: none"
    fi

    avail=$(free_kib /) || avail=0
    n=$(free_kib /srv) || n=0
    echo "Free space: / $(gib "$avail") GiB, /srv $(gib "$n") GiB"

    commit=
    if [ -f "$home/state/deploy-files.commit" ]; then
        IFS= read -r -t 5 commit < "$home/state/deploy-files.commit" || true
    fi
    if [ -n "$commit" ]; then
        echo "Deploy files: from ${commit:0:12}"
    else
        echo "Deploy files: none installed by deploy.sh or bootstrap.sh yet"
    fi

    user=$(getent passwd 1654 | cut -d: -f1) || user=
    group=$(getent group 1654 | cut -d: -f1) || group=
    if [ "$user" = minerbot-app ] && [ "$group" = minerbot-app ]; then
        echo "UID and GID 1654: reserved for the containers (minerbot-app)"
    else
        echo "UID and GID 1654: user ${user:-free}, group ${group:-free}. They should both be minerbot-app, which keeps them for the containers; bootstrap.sh reserves them."
    fi

    old=$(find "$home/state/tmp" -mindepth 1 -maxdepth 1 -mtime +0 -printf '%f ' 2> /dev/null) || old=
    if [ -n "$old" ]; then
        echo "state/tmp: left over by a run that was cut off, more than a day ago: $old(the next setup run removes them)"
    fi
}

# ---- The nightly check

check() {
    local line out running health pid listeners n avail ends wait=60
    local -a problems=()
    if test_fast; then
        wait=1
    fi
    while IFS= read -r -t 5 line; do
        if [ -n "$line" ]; then
            problems+=("$line")
        fi
    done < <(backup_problems)

    if [ -f "$nginx_secrets/origin-cert.pem" ] \
        && ! run_for 10 openssl x509 -noout -checkend 2592000 -in "$nginx_secrets/origin-cert.pem" > /dev/null 2>&1; then
        ends=$(cert_end "$nginx_secrets/origin-cert.pem") || ends="soon"
        problems+=("The Origin certificate ends $ends, within 30 days: make a new one in Cloudflare and run ./setup-dashboard.sh.")
    fi

    # Once, then again a minute later, so a reload in progress isn't reported
    if ! out=$(run_for 30 nginx -t -q 2>&1); then
        sleep "$wait"
        if ! out=$(run_for 30 nginx -t -q 2>&1); then
            problems+=("nginx -t fails, twice a minute apart, so every reload of nginx fails, AutoSSL's included: ${out%%$'\n'*}")
        fi
    fi

    out=$(run_for 30 docker container inspect -f '{{.State.Running}}' "$bot_container" 2>&1) || true
    case $out in
        true) ;;
        false) problems+=("The bot isn't running. Its log: docker logs --tail 50 $bot_container") ;;
        *"No such"*) problems+=("The bot's container, $bot_container, doesn't exist.") ;;
        *) problems+=("Docker didn't answer about the bot: ${out%%$'\n'*}") ;;
    esac

    pid=0
    if [ -f "$home/dashboard.env" ]; then
        out=$(run_for 30 docker container inspect \
            -f '{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.State.Pid}}' \
            "$dashboard_container" 2>&1) || true
        running='' health='' pid=''
        read -r -t 5 running health pid <<< "$out" || true
        if [ "$running" != true ]; then
            problems+=("The dashboard isn't running. Its log: docker logs --tail 50 $dashboard_container")
        elif [ "$health" = unhealthy ]; then
            problems+=("The dashboard is unhealthy. Its log: docker logs --tail 50 $dashboard_container")
        fi
    fi
    listeners=$(run_for 10 ss -Hltnp 'sport = :5080' 2> /dev/null) || listeners=
    if [ -n "$listeners" ] && ! listener_is "${pid:-0}" "$listeners"; then
        problems+=("Something other than the dashboard's container listens on port 5080, where nginx sends the dashboard's visitors: ${listeners%%$'\n'*}")
    fi

    if [ ! -f "$cloudflare_list" ]; then
        problems+=("cPanel's list of Cloudflare's addresses, $cloudflare_list, is missing, so nginx refuses every visitor to the dashboard.")
    else
        n=$(grep -c '^[[:space:]]*set_real_ip_from[[:space:]]' "$cloudflare_list") || n=0
        if [ "$n" = 0 ]; then
            problems+=("cPanel's list of Cloudflare's addresses, $cloudflare_list, is empty, so nginx refuses every visitor to the dashboard.")
        fi
    fi

    if [ -e "$home/state/nginx-change" ]; then
        problems+=("An nginx change was cut off. Run ./setup-dashboard.sh, which undoes it first.")
    fi

    avail=$(free_kib /srv) || avail=0
    if [ "$avail" -lt 5242880 ]; then
        problems+=("Less than 5 GiB is free on /srv: $(gib "$avail") GiB. README.txt says how to make room.")
    fi
    avail=$(free_kib /) || avail=0
    if [ "$avail" -lt 2097152 ]; then
        problems+=("Less than 2 GiB is free on /: $(gib "$avail") GiB. README.txt says how to make room.")
    fi

    if [ ${#problems[@]} -gt 0 ]; then
        printf '%s\n' "${problems[@]}"
        exit 1
    fi
    exit 0
}

mail_test() {
    printf 'To: root\nSubject: MinerBot mail test\n\nThis is the test message from /opt/minerbot/status.sh --mail-test. It arrived, so the mail cron sends about a failed backup or a problem in the nightly check reaches you too.\n' \
        | run_for 30 /usr/sbin/sendmail -t || die "The mail system didn't take the test message (above)."
    echo "Sent a test message to root. It should arrive at the server's contact address within a few minutes. If it doesn't, check the contact email in WHM's Basic WebHost Manager Setup."
}

look() {
    echo "nginx's http-level lines, from every file nginx reads, without server blocks and without any secret:"
    nginx_look || die "The look needs a configuration nginx accepts. nginx -t shows what's wrong."
    echo
    echo "nginx.conf's main-context user, load_module and include lines, which the standalone test copies:"
    main_context /etc/nginx/nginx.conf
    echo
    echo "nginx's build flags:"
    run_for 30 nginx -V 2>&1 | tr ' ' '\n' | grep -E 'user=|group=|realip|v2|dynamic' || true
}

case $form in
    show) show ;;
    check) check ;;
    mail-test) mail_test ;;
    nginx-look) look ;;
esac

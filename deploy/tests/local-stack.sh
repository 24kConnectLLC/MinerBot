#!/usr/bin/env bash
# A manual test of the whole stack on one machine, laid out as on the server: MariaDB 11.4, the dashboard image and
# nginx 1.31.8 share one loopback, as host networking gives them there, so 127.0.0.1:3306 behaves the same. The dashboard
# listens on no port, only on its socket /run/minerbot-dashboard/dashboard.sock, in a folder of mode 2750 owned by 1654
# and the group nobody (65534), which nginx's workers run as, as on the server. A Windows folder can't hold a socket, so
# that folder is a volume, mounted at the same path in the dashboard, nginx and the holder. A holder container owns the
# loopback, on a user network of its own, and the other three join it, so this machine's own ports are never used. The
# holder also stands for the server itself: the checks ask the socket there as root, as deploy.sh does, and as other
# accounts, as a cPanel site could. Two more containers make the requests: one at the address in the stand-in Cloudflare
# list, which sends CF-Connecting-IP as Cloudflare does, and one elsewhere. It isn't in CI.
#
#   bash deploy/tests/local-stack.sh up               Builds mbtest-dashboard:local as the server builds the dashboard,
#                                                     if it's missing, then starts the stack
#   bash deploy/tests/local-stack.sh check            One line per check, then "N of M passed". Exit 1 if any failed
#   bash deploy/tests/local-stack.sh down [--images]  Removes everything up and check made. --images also removes the
#                                                     image, the build cache up's build made, and images up pulled
#
# Run it from Git Bash or Linux. It needs Docker with Compose, and openssl. It keeps its files in mbtest-stack/ in
# TMPDIR, or /tmp, so give all three the same TMPDIR. On Windows, point TMPDIR at a folder on a drive with room.
#
# Everything it makes in Docker is named mbtest-stack-... . Every container of the stack shares the holder's processes,
# so the whole stack stops by itself 2 hours after up (MINERBOT_TEST_STACK_LIFETIME seconds), even if down never runs;
# down removes what's left. check makes its stand-in bot with Compose and removes it before it ends. Discord's sign-in
# itself is covered by the dashboard's own tests, with a fake Discord.
set -Eeuo pipefail
umask 077
# Git Bash would otherwise turn the container paths and openssl's -subj below into Windows paths
export MSYS_NO_PATHCONV=1
# A project name from the environment would win over the compose files' own
unset COMPOSE_PROJECT_NAME

here=$(cd "$(dirname "$0")" && pwd)
deploy=$(cd "$here/.." && pwd)
repo=$(cd "$deploy/.." && pwd)

# The server's own functions: apply_rights, env_safe, env_value, bot_unchanged, start_service and the image helpers,
# revision among them; and its names, socket_dir and dashboard_socket among them. Sourcing runs nothing, but sets the
# server's PATH, which on Windows has no docker, so this PATH comes back.
path_before=$PATH
# shellcheck source=../common.sh
source "$deploy/common.sh"
PATH=$path_before

usage() {
    echo "Usage: bash deploy/tests/local-stack.sh up | check | down [--images]"
    exit 1
}

# A path as Docker reads it: G:/... in Git Bash, where docker is a Windows program, and the same path elsewhere
native() {
    if command -v cygpath > /dev/null 2>&1; then
        cygpath -m "$1"
    else
        printf '%s\n' "$1"
    fi
}

state=$(native "${TMPDIR:-/tmp}")/mbtest-stack
stack=mbtest-stack
label=mbtest-stack=1
net=$stack-net
# No other test script uses this range
subnet=172.31.252.0/24
host_ip=172.31.252.2
cloudflare_ip=172.31.252.10
elsewhere_ip=172.31.252.20
holder=$stack-host
db=$stack-db
dashboard=$stack-dashboard
nginx=$stack-nginx
cloudflare=$stack-cloudflare
elsewhere=$stack-elsewhere
keys_volume=$stack-keys
# The socket's folder, socket_dir from common.sh, as a volume at the same path in the holder, the dashboard and nginx,
# and read-only in the container that runs deploy.sh's readiness rule. Docker never copies anything into it, so it keeps
# the mode, owner and group the holder gives it.
socket_volume=$stack-socket
socket_mount=(--mount "type=volume,source=$socket_volume,target=$socket_dir,volume-nocopy=true")
socket_mount_ro=(--mount "type=volume,source=$socket_volume,target=$socket_dir,volume-nocopy=true,readonly=true")
# nobody, nginx's workers' account on the server, and Apache's on cPanel; and an account like any cPanel site's
nobody_account=65534:65534
other_account=4001:4001
# The server's OS, as scripts-test.sh has it, where common.sh's socket_problem runs as on the server: there, as here,
# 65534 is named nobody
alma_image=almalinux:9
image=mbtest-dashboard:local
mariadb_image=mariadb:11.4
# The server's nginx version, as in nginx-test.sh. Its image has curl, so it also serves for the holder and the clients.
nginx_image=nginx:1.31.8-alpine
# The stand-in bot runs sleep in the .NET runtime image, which has no entrypoint of its own
runtime_image=mcr.microsoft.com/dotnet/runtime:10.0
bot_image=$stack-bot
bot_volume=$stack-data
# Made-up names, as in the database tests, and made-up Discord IDs, as in the dashboard's tests
site_db=website_db
bot_db=minerbot_db
bot_login=mbtest_bot
owner_id=1000000000000000001
client_id=1000000000000000009
public=$public_host
# The visitor's address, as Cloudflare's CF-Connecting-IP header gives it
visitor=203.0.113.7
# The line that includes pipelog.conf, give or take spaces
pipelog_include='[[:space:]]*include /etc/nginx/minerbot/pipelog\.con\[f\];[[:space:]]*'
lifetime=${MINERBOT_TEST_STACK_LIFETIME:-7200}
[[ $lifetime =~ ^[1-9][0-9]*$ ]] || { echo "MINERBOT_TEST_STACK_LIFETIME must be a number of seconds."; exit 1; }
# Every container but the clients joins the holder's loopback, and every one shares its processes, so all of them end
# when the holder's sleep does
joined=(--label "$label" --network "container:$holder" --pid "container:$holder")

# The functions from common.sh work on this stack, never on the server's names
home=$state/project
bot_container=$stack-bot
dashboard_container=$dashboard

dk() {   # dk <seconds> <docker arguments...>: docker with a time limit
    local limit=$1
    shift
    run_for "$limit" docker "$@"
}

say() {
    printf '%s\n' "$*"
}

stop() {   # stop <why>: ends this command here; check also says how far it got
    echo "STOPPED: $1"
    if [ -n "${checks:-}" ]; then
        echo "$passed of $checks passed"
    fi
    exit 1
}

wait_for() {   # wait_for <seconds> <command...>: runs the command every second until it succeeds, for at most that long
    local limit=$1 started=$SECONDS
    shift
    until "$@"; do
        [ $((SECONDS - started)) -lt "$limit" ] || return 1
        sleep 1
    done
}

docker_answers() {
    dk 30 version > /dev/null 2>&1 || stop "Docker isn't running, or doesn't answer."
    dk 30 compose version > /dev/null 2>&1 || stop "Docker Compose isn't there."
}

running() {   # running <container>
    [ "$(dk 30 container inspect -f '{{.State.Running}}' "$1" 2> /dev/null)" = true ]
}

# This stack's containers, networks and volumes that exist now, on one line
leftovers() {
    {
        dk 30 ps -a --filter "name=$stack-" --format '{{.Names}}' 2> /dev/null || true
        dk 30 ps -a --filter "label=$stack" --format '{{.Names}}' 2> /dev/null || true
        dk 30 network ls --filter "name=$stack-" --format '{{.Name}}' 2> /dev/null || true
        dk 30 volume ls --filter "name=$stack-" --format '{{.Name}}' 2> /dev/null || true
    } | awk 'NF' | LC_ALL=C sort -u | paste -sd ' ' -
}

# The images this stack made that exist now: the stand-in bot's tags, and with --images the dashboard image
image_leftovers() {
    {
        dk 30 images --format '{{.Repository}}:{{.Tag}}' "$bot_image" 2> /dev/null || true
        if [ "${1:-}" = --images ] && dk 30 image inspect "$image" > /dev/null 2>&1; then
            echo "$image"
        fi
    } | awk 'NF' | LC_ALL=C sort -u | paste -sd ' ' -
}

# Pulls an image when it's missing, and notes that this stack pulled it, so down --images removes it again
have_image() {   # have_image <image>
    dk 30 image inspect "$1" > /dev/null 2>&1 && return 0
    say "Pulling $1"
    mkdir -p "$state" "$state/image"
    dk 300 pull -q "$1" > /dev/null || return 1
    printf '%s\n' "$1" >> "$state/image/pulled"
}

made_up() {   # made_up <length> <tr characters>: a random value for this stack alone
    local value=''
    while [ ${#value} -lt "$1" ]; do
        value+=$(head -c 256 /dev/urandom | LC_ALL=C tr -dc "$2")
    done
    printf '%s\n' "${value:0:$1}"
}

connection() {   # connection <database> <login> <password>: a connection string in the form setup-dashboard.sh writes
    printf 'Server=127.0.0.1;Port=3306;Database=%s;Uid=%s;Pwd=%s;SslMode=Disabled;Connection Timeout=15;CharSet=utf8mb4;Maximum Pool Size=6;\n' \
        "$1" "$2" "$3"
}

load_secrets() {   # the made-up values up wrote, for check
    local line
    [ -f "$state/secrets" ] || stop "There's no stack from up in $state. Run up first, with the same TMPDIR."
    while IFS= read -r -t 5 line; do
        case $line in
            PROXY_KEY=*) proxy_key=${line#*=} ;;
            DB_PASSWORD=*) db_password=${line#*=} ;;
            CLIENT_SECRET=*) client_secret=${line#*=} ;;
            BOT_PASSWORD=*) bot_password=${line#*=} ;;
            ROOT_PASSWORD=*) root_password=${line#*=} ;;
        esac
    done < "$state/secrets"
    if [ -z "${proxy_key:-}" ] || [ -z "${db_password:-}" ] || [ -z "${client_secret:-}" ] \
        || [ -z "${bot_password:-}" ] || [ -z "${root_password:-}" ]; then
        stop "$state/secrets isn't complete. Run down, then up."
    fi
}

# SQL on stdin as MariaDB's root, in its container, the way root runs it on the server
sql() {   # sql <mariadb arguments...>
    dk 60 exec -i "$db" mariadb --batch "$@"
}

# A bash command in MariaDB's container as root, with a copy of common.sh sourced and SITE_DB and BOT_DB set, as
# setup-dashboard.sh and deploy.sh run the rights functions on the server
in_database() {   # in_database <bash command>
    dk 300 exec -e "SITE_DB=$site_db" -e "BOT_DB=$bot_db" "$db" \
        bash -c "set -Eeuo pipefail; umask 077; source /tmp/rights/common.sh; home=/tmp/rights; $1"
}

# check_settings in common.sh, with this stack's names: the image's own validator, with no network, a read-only root, and
# tmpfs for /tmp and the keys folder, and the socket compose.yml gives the dashboard. Its output is the caller's.
check_settings_on() {   # check_settings_on <env file>
    local status=0
    dk 30 rm -f -v "$stack-check" > /dev/null 2>&1 || true
    run_for 60 docker run --rm --pull never --name "$stack-check" --network none --read-only \
        --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --tmpfs /app/keys:rw,noexec,nosuid,nodev,size=1m,mode=1777 \
        --cap-drop ALL --security-opt no-new-privileges:true --memory 256m --pids-limit 64 \
        --env-file "$1" -e "Dashboard__UnixSocket=$dashboard_socket" "$image" check-settings || status=$?
    dk 30 rm -f -v "$stack-check" > /dev/null 2>&1 || true
    return "$status"
}

# A request from inside the stack's loopback, as something else on the server would make it
loopback() {   # loopback <curl arguments...>
    dk 20 exec "$holder" curl -s --max-time 10 "$@" 2>&1 || true
}

# A request on the dashboard's socket, as root makes it on the server: deploy.sh's readiness check, say
on_socket() {   # on_socket <curl arguments...>
    dk 20 exec "$holder" curl -s --max-time 10 --unix-socket "$dashboard_socket" "$@" 2>&1 || true
}

# The same with nginx's proxy key, which goes in on stdin, so it's never in a command line
on_socket_with_key() {   # on_socket_with_key <curl arguments...>
    printf 'X-MinerBot-Proxy-Key: %s\n' "$proxy_key" \
        | dk 20 exec -i "$holder" curl -s --max-time 10 --unix-socket "$dashboard_socket" -H @- "$@" 2>&1 || true
}

# The same as another account on the server, without the key
on_socket_as() {   # on_socket_as <uid:gid> <curl arguments...>
    local account=$1
    shift
    dk 20 exec -u "$account" "$holder" curl -s --max-time 10 --unix-socket "$dashboard_socket" "$@" 2>&1 || true
}

dashboard_ready() {   # Docker says healthy, and /health/ready answers Healthy through the socket, which proves both databases
    [ "$(dk 30 container inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$dashboard" 2> /dev/null)" = healthy ] \
        && [ "$(on_socket -H 'Host: localhost' http://localhost/health/ready)" = Healthy ]
}

request() {   # request <client> <curl arguments...>: nginx's answer, headers first, or curl's error
    local client=$1
    shift
    dk 30 exec "$client" curl -sSk -i --max-time 10 \
        --resolve "$public:443:$host_ip" --resolve "$public:80:$host_ip" "$@" 2>&1 || true
}

from_cloudflare() {   # from_cloudflare <curl arguments...>: a visitor's request, as Cloudflare passes it on
    request "$cloudflare" -H "CF-Connecting-IP: $visitor" "$@"
}

header_count() {   # header_count <name> <response>: how many header lines have that name, in any letter case
    printf '%s\n' "$2" | tr -d '\r' | awk -v name="$1" 'BEGIN { name = tolower(name); n = 0 }
        index(tolower($0), name ":") == 1 { n++ } END { print n }'
}

header_value() {   # header_value <name> <response>: the first such header's value
    printf '%s\n' "$2" | tr -d '\r' | awk -v name="$1" 'BEGIN { name = tolower(name) }
        index(tolower($0), name ":") == 1 { sub(/^[^:]*:[ \t]*/, ""); print; exit }'
}

# Has nginx read its files again, and waits up to 10 s until no worker runs the old ones
reload_nginx() {
    # shellcheck disable=SC2016 # the $ are for the container's shell
    dk 30 exec "$nginx" sh -c 'old=$(pgrep -P "$(cat /run/nginx.pid)") && nginx -s reload 2>&1 || exit 1
        for i in $(seq 100); do
            left=""
            for worker in $old; do if [ -d "/proc/$worker" ]; then left=$worker; fi; done
            if [ -z "$left" ]; then exit 0; fi
            sleep 0.1
        done
        echo "a worker with the old files still ran 10 seconds after the reload"
        exit 1'
}

# ---------- up

# Builds the image as the server builds the dashboard, when it isn't there, and notes the build cache records the build
# made, so down --images can remove exactly those
build_image() {
    local before after status=0
    if dk 30 image inspect "$image" > /dev/null 2>&1; then
        say "Using $image, which is already there. down --images removes it."
        return 0
    fi
    say "Building $image as the server builds the dashboard, within 30 minutes. Its log: $state/image/build.log"
    before=$(dk 60 buildx du --format '{{.ID}}' 2> /dev/null) || before=''
    (cd "$repo" && run_for -k 30 1800 docker build --network host --cpu-quota 200000 --cpu-period 100000 \
        -f MinerBot.Dashboard/Dockerfile -t "$image" .) > "$state/image/build.log" 2>&1 || status=$?
    # Noted even when the build failed, so down --images also removes what a failed build left
    if after=$(dk 60 buildx du --format '{{.ID}}' 2> /dev/null); then
        {
            cat "$state/image/cache-ids" 2> /dev/null || true
            LC_ALL=C comm -13 <(printf '%s\n' "$before" | awk 'NF' | LC_ALL=C sort) \
                <(printf '%s\n' "$after" | awk 'NF' | LC_ALL=C sort)
        } | awk 'NF' | LC_ALL=C sort -u > "$state/image/cache-ids.new"
        mv -f "$state/image/cache-ids.new" "$state/image/cache-ids"
    else
        say "docker buildx du didn't answer, so the build cache this build made isn't recorded, and down --images leaves it."
    fi
    if [ "$status" != 0 ]; then
        tail -n 30 "$state/image/build.log"
        stop "The image didn't build (the end of its log is above). down --images removes the build cache it made."
    fi
    echo yes > "$state/image/built"
}

write_env_file() {   # write_env_file <file> <NAME=value>...: every value must pass common.sh's env_safe, as setup's do
    local file=$1 line
    shift
    : > "$file"
    for line in "$@"; do
        env_safe "${line#*=}" || stop "The made-up value for ${line%%=*} breaks the safe-character rule."
        printf '%s\n' "$line" >> "$file"
    done
}

make_files() {
    proxy_key=$(made_up 64 0-9a-f)
    db_password=$(made_up 40 A-Za-z0-9)
    client_secret=$(made_up 32 A-Za-z0-9_-)
    bot_password=$(made_up 40 A-Za-z0-9)
    root_password=$(made_up 32 0-9a-f)
    printf 'PROXY_KEY=%s\nDB_PASSWORD=%s\nCLIENT_SECRET=%s\nBOT_PASSWORD=%s\nROOT_PASSWORD=%s\n' \
        "$proxy_key" "$db_password" "$client_secret" "$bot_password" "$root_password" > "$state/secrets"
    # Made up, in the form setup-dashboard.sh writes, with Cloudflare Access off
    write_env_file "$state/dashboard.env" \
        "Dashboard__PublicOrigin=https://$public" \
        "Dashboard__ProxyKey=$proxy_key" \
        "Dashboard__Owners=$owner_id" \
        "Dashboard__Admins=" \
        "Discord__ClientId=$client_id" \
        "Discord__ClientSecret=$client_secret" \
        "CloudflareAccess__TeamDomain=" \
        "CloudflareAccess__Audience=" \
        "ConnectionStrings__DefaultConnection=$(connection "$site_db" "$dashboard_login" "$db_password")" \
        "ConnectionStrings__BotConnection=$(connection "$bot_db" "$dashboard_login" "$db_password")"
    # The one line of bot.env the migrations use, with the bot-like login
    write_env_file "$state/bot.env" "ConnectionStrings__BotConnection=$(connection "$bot_db" "$bot_login" "$bot_password")"
}

start_holder() {
    local out
    out=$(dk 60 network create --subnet "$subnet" --label "$label" "$net" 2>&1) \
        || stop "Couldn't make the network $net: $out. Another network may hold $subnet; see docker network ls."
    out=$(dk 60 volume create --label "$label" "$socket_volume" 2>&1) || stop "Couldn't make the volume $socket_volume: $out"
    # --init reaps what ends in the shared process namespace. When sleep ends, the namespace ends, and every container in
    # it with it.
    out=$(dk 60 run -d --rm --init --name "$holder" --label "$label" --network "$net" --ip "$host_ip" \
        --sysctl net.ipv6.conf.all.disable_ipv6=0 "${socket_mount[@]}" "$nginx_image" sleep "$lifetime" 2>&1) \
        || stop "Couldn't start $holder: $out"
    stops_at=$(date -u -d "@$(($(date +%s) + lifetime))" +%Y-%m-%dT%H:%M:%SZ)
}

# The socket's folder as systemd-tmpfiles makes it on the server, from deploy/minerbot-dashboard.tmpfiles: 2750, owned by
# 1654, the dashboard, and 65534, the group nobody. Only the dashboard can make a file there, and only nobody's group, of
# nginx's workers, can enter. The set-group-ID bit gives the socket that group.
lay_out_socket_folder() {
    local out
    out=$(dk 30 exec "$holder" sh -c "chown 1654:65534 $socket_dir && chmod 2750 $socket_dir" 2>&1) \
        || stop "Couldn't lay out the socket's folder $socket_dir in $socket_volume: $out"
}

start_clients() {
    local out
    out=$(dk 60 run -d --rm --name "$cloudflare" --label "$label" --network "$net" --ip "$cloudflare_ip" \
        --pid "container:$holder" "$nginx_image" sleep "$lifetime" 2>&1) || stop "Couldn't start $cloudflare: $out"
    out=$(dk 60 run -d --rm --name "$elsewhere" --label "$label" --network "$net" --ip "$elsewhere_ip" \
        --pid "container:$holder" "$nginx_image" sleep "$lifetime" 2>&1) || stop "Couldn't start $elsewhere: $out"
}

database_answers() {
    dk 15 exec "$db" healthcheck.sh --connect --innodb_initialized > /dev/null 2>&1
}

start_database() {
    local out
    # latin1 is the server's default character set, as in the database tests. The root password goes in by name, from
    # this command's environment.
    out=$(MARIADB_ROOT_PASSWORD=$root_password dk 60 run -d --name "$db" "${joined[@]}" -e MARIADB_ROOT_PASSWORD \
        "$mariadb_image" --character-set-server=latin1 2>&1) || stop "Couldn't start MariaDB: $out"
    # Its data is in an anonymous volume, which down removes and then checks by name
    dk 30 container inspect -f '{{range .Mounts}}{{if eq .Type "volume"}}{{println .Name}}{{end}}{{end}}' "$db" \
        >> "$state/volumes" || stop "Docker didn't say which volume MariaDB's container has."
    wait_for 120 database_answers \
        || stop "MariaDB wasn't ready within 120 seconds. The end of its log: $(dk 30 logs --tail 20 "$db" 2>&1)"
    # Root's own option file, as on the server, so the mariadb client in the container is root without a password in
    # its arguments. It goes in on stdin.
    out=$(printf '[client]\nuser=root\npassword=%s\n' "$root_password" \
        | dk 30 exec -i "$db" sh -c 'umask 077 && cat > /root/.my.cnf' 2>&1) \
        || stop "Couldn't write root's option file in MariaDB's container: $out"
}

prepare_database() {
    local out
    out=$(sql <<< "CREATE DATABASE \`$site_db\`; CREATE DATABASE \`$bot_db\`;" 2>&1) \
        || stop "MariaDB didn't make the two databases: $out"
    out=$(tr -d '\r' < "$repo/docs/schema/wordpress-tables.sql" | sql "$site_db" 2>&1) \
        || stop "MariaDB didn't take the website's tables from docs/schema/wordpress-tables.sql: $out"
    # A login like the bot's: everything in MinerBot's database, which the migrations need, and the rows of the three
    # website tables. Its password goes in on stdin, and the client never prints a failing statement.
    out=$(sql --skip-print-query-on-error 2>&1 << SQL
CREATE USER '$bot_login'@'127.0.0.1' IDENTIFIED BY '$bot_password';
GRANT ALL PRIVILEGES ON \`$bot_db\`.* TO '$bot_login'@'127.0.0.1';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`$site_db\`.\`ihvph_armor_styles\` TO '$bot_login'@'127.0.0.1';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`$site_db\`.\`ihvph_blacklist\` TO '$bot_login'@'127.0.0.1';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`$site_db\`.\`ihvph_discord_servers\` TO '$bot_login'@'127.0.0.1';
SQL
    ) || stop "MariaDB didn't make the bot-like login: $out"
}

# The migrations with the bot-like login, in run_migrate's exact form: bot.env's value read by env_value, put into this
# one command's environment and passed on by name. Only the network and the names differ: the stack's loopback instead
# of the host's, and this stack's image.
migrate() {
    local bot_connection out status=0
    bot_connection=$(env_value "$state/bot.env" ConnectionStrings__BotConnection) \
        || stop "env_value refused the made-up bot.env (above)."
    [ -n "$bot_connection" ] || stop "env_value found no ConnectionStrings__BotConnection in the made-up bot.env."
    dk 30 rm -f -v "$stack-migrate" > /dev/null 2>&1 || true
    out=$(ConnectionStrings__BotConnection=$bot_connection run_for 180 docker run --rm --pull never --name "$stack-migrate" \
        --network "container:$holder" --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 --cap-drop ALL \
        --security-opt no-new-privileges:true --memory 256m --pids-limit 64 \
        -e ConnectionStrings__BotConnection "$image" migrate 2>&1) || status=$?
    dk 30 rm -f -v "$stack-migrate" > /dev/null 2>&1 || true
    printf '%s\n' "$out" > "$state/logs/migrate.log"
    if [ "$status" != 0 ] || [[ $out != *"MinerBot's database is at version"* ]]; then
        printf '%s\n' "$out"
        stop "The migrations didn't run (above)."
    fi
    say "Migrated with the bot-like login: ${out##*MinerBot\'s database is at }"
}

# The dashboard's login on both its hosts, as setup-dashboard.sh makes it, then the real apply_rights: the rehearsal on a
# locked throwaway login, the apply, and the comparison
login_and_rights() {
    local out
    out=$(sql --skip-print-query-on-error 2>&1 << SQL
CREATE USER '$dashboard_login'@'localhost' IDENTIFIED BY '$db_password';
CREATE USER '$dashboard_login'@'127.0.0.1' IDENTIFIED BY '$db_password';
SQL
    ) || stop "MariaDB didn't make the dashboard's login: $out"
    mkdir -p "$state/rights"
    cp "$deploy/common.sh" "$deploy/dashboard-rights.sql" "$deploy/rights-list.sql" "$state/rights/"
    out=$(cd "$state" && dk 60 cp rights "$db:/tmp/" 2>&1) || stop "Couldn't copy the rights files into MariaDB's container: $out"
    out=$(in_database 'apply_rights /tmp/rights/dashboard-rights.sql' 2>&1) || {
        printf '%s\n' "$out"
        stop "apply_rights failed (above)."
    }
    printf '%s\n' "$out" > "$state/logs/rights.log"
    say "$out"
}

settings_pass() {
    local out
    out=$(check_settings_on "$state/dashboard.env" 2>&1) || {
        printf '%s\n' "$out"
        stop "check-settings refused the made-up dashboard.env (above)."
    }
    printf '%s\n' "$out" > "$state/logs/check-settings.log"
    [ "$out" = "The dashboard's settings are fine." ] || stop "check-settings passed, but said: $out"
    say "check-settings: $out"
}

start_dashboard() {
    local out started=$SECONDS
    # compose.yml's settings for the dashboard, as docker run options: Dashboard__UnixSocket from its environment, which
    # wins over dashboard.env, and the socket's folder at the same path. No restart policy: a test stack mustn't come back
    # by itself after Docker restarts.
    out=$(dk 60 run -d --name "$dashboard" "${joined[@]}" --user 1654:1654 --env-file "$state/dashboard.env" \
        -e "Dashboard__UnixSocket=$dashboard_socket" --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m,mode=1777 \
        -v "$keys_volume:/app/keys" "${socket_mount[@]}" \
        --security-opt no-new-privileges:true --cap-drop ALL --memory 512m --memory-swap 512m --cpus 1 \
        --pids-limit 256 --stop-timeout 30 --log-driver json-file --log-opt max-size=10m --log-opt max-file=5 \
        "$image" 2>&1) || stop "Couldn't start the dashboard: $out"
    wait_for 90 dashboard_ready \
        || stop "The dashboard wasn't ready within 90 seconds. The end of its log: $(dk 30 logs --tail 20 "$dashboard" 2>&1)"
    say "The dashboard was ready $((SECONDS - started)) seconds after it started: Docker says healthy, and /health/ready answers Healthy through its socket."
}

nginx_answers() {   # a local request isn't Cloudflare, so nginx refuses it
    [ "$(loopback -k -o /dev/null -w '%{http_code}' --resolve "$public:443:127.0.0.1" "https://$public/")" = 403 ]
}

start_nginx() {
    local out count
    count=$(grep -cxE "$pipelog_include" "$deploy/minerbot-dashboard.conf" || true)
    [ "$count" = 2 ] || stop "deploy/minerbot-dashboard.conf includes pipelog.conf $count time(s), not 2."
    mkdir -p "$state/nginx" "$state/nginx/conf.d" "$state/nginx/conf.d/includes-optional" "$state/nginx/minerbot"
    # The real file, as it is. cPanel's pipelog module isn't in nginx's image, so pipelog.conf is empty, as setup writes
    # it on a server where WHM's piped logging is off.
    tr -d '\r' < "$deploy/minerbot-dashboard.conf" > "$state/nginx/conf.d/minerbot-dashboard.conf"
    : > "$state/nginx/minerbot/pipelog.conf"
    # The shape of cPanel's list of Cloudflare's addresses, with the one client's address
    printf 'set_real_ip_from %s;\nreal_ip_header CF-Connecting-IP;\n' "$cloudflare_ip" \
        > "$state/nginx/conf.d/includes-optional/cloudflare.conf"
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$proxy_key" > "$state/nginx/minerbot/proxy-key.conf"
    out=$(cd "$state/nginx/minerbot" && run_for 60 openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=$public" \
        -addext "subjectAltName=DNS:$public" -keyout origin-key.pem -out origin-cert.pem 2>&1) \
        || stop "openssl couldn't make the stand-in certificate: $out"
    out=$(dk 60 create --name "$nginx" "${joined[@]}" "${socket_mount[@]}" "$nginx_image" 2>&1) \
        || stop "Couldn't make nginx's container: $out"
    # As on the server, nginx's workers run as nobody, whose group may enter the socket's folder. The image's own
    # nginx.conf says user nginx, and nothing else in it changes.
    out=$(cd "$state/nginx" && dk 60 cp "$nginx:/etc/nginx/nginx.conf" nginx.conf.image 2>&1) \
        || stop "Couldn't read nginx.conf from nginx's container: $out"
    tr -d '\r' < "$state/nginx/nginx.conf.image" | sed 's/^user[[:space:]][[:space:]]*nginx;$/user nobody;/' \
        > "$state/nginx/nginx.conf"
    if [ "$(grep -c '^[[:space:]]*user[[:space:]]' "$state/nginx/nginx.conf" || true)" != 1 ] \
        || [ "$(grep -cx 'user nobody;' "$state/nginx/nginx.conf" || true)" != 1 ]; then
        stop "The nginx.conf of $nginx_image doesn't have the one line \"user nginx;\" this script makes \"user nobody;\"."
    fi
    # Relative paths, which docker reads the same way in Git Bash and in Linux
    out=$(cd "$state/nginx" && dk 60 cp conf.d/. "$nginx:/etc/nginx/conf.d/" 2>&1 \
        && dk 60 cp nginx.conf "$nginx:/etc/nginx/nginx.conf" 2>&1 \
        && dk 60 cp minerbot "$nginx:/etc/nginx/minerbot" 2>&1) || stop "Couldn't copy the files into nginx's container: $out"
    out=$(dk 60 start "$nginx" 2>&1) || stop "nginx didn't start: $out"
    # As on the server, only root can open the certificate's key and the proxy key: nginx's master reads them as root
    out=$(dk 30 exec "$nginx" sh -c 'chmod 700 /etc/nginx/minerbot && chmod 600 /etc/nginx/minerbot/* && nginx -t -q 2>&1') \
        || stop "nginx -t refused the files: $out"
    wait_for 30 nginx_answers || stop "nginx didn't answer 403 to a local request within 30 seconds."
}

up() {
    local left answer
    docker_answers
    left=$(leftovers)
    [ -z "$left" ] || stop "Something from an earlier run is still there: $left. Remove it with: bash deploy/tests/local-stack.sh down"
    # Up's own files from before go; image/ stays, because down --images needs it
    if [ -d "$state" ]; then
        find "$state" -mindepth 1 -maxdepth 1 ! -name image -exec rm -rf {} +
    fi
    mkdir -p "$state" "$state/logs" "$state/image"
    have_image "$mariadb_image" || stop "Couldn't pull $mariadb_image within 5 minutes."
    have_image "$nginx_image" || stop "Couldn't pull $nginx_image within 5 minutes."
    build_image
    make_files
    start_holder
    lay_out_socket_folder
    start_clients
    start_database
    prepare_database
    migrate
    login_and_rights
    settings_pass
    start_dashboard
    start_nginx
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/")
    [[ $answer == "HTTP/1.1 200"* ]] || stop "The home page didn't answer 200 through nginx: $answer"
    say "The stack is up, and the home page answers 200 through nginx. It stops by itself at $stops_at."
    say "Next: bash deploy/tests/local-stack.sh check"
}

# ---------- check

problem() {   # problem <what's wrong> [<what came back>]: notes why the current check fails
    notes+="    $1"$'\n'
    if [ -n "${2:-}" ]; then
        notes+=$(sed -n '1,40{s/\r$//;s/^\(.\{160\}\).*/\1 .../;s/^/    | /;p}' <<< "$2")$'\n'
    fi
}

expect() {   # expect <text> <answer> <what>: the answer must hold the text
    grep -qF -- "$1" <<< "$2" || problem "$3: expected '$1'" "$2"
}

refuse() {   # refuse <text> <answer> <what>: the answer must not hold the text
    if grep -qF -- "$1" <<< "$2"; then problem "$3: '$1' shouldn't be there" "$2"; fi
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

put_key_back() {
    dk 30 exec "$nginx" mv /etc/nginx/minerbot/proxy-key.off /etc/nginx/minerbot/proxy-key.conf && reload_nginx > /dev/null
}

start_dashboard_again() {
    dk 60 start "$dashboard" > /dev/null 2>&1 && wait_for 90 dashboard_ready
}

# What a check cut off halfway can leave: nginx's key file renamed, the dashboard stopped or killed, or a file another
# account made in the socket's folder, which only a defect lets it make
put_back() {
    if dk 30 exec "$nginx" test -e /etc/nginx/minerbot/proxy-key.off > /dev/null 2>&1; then
        put_key_back || echo "Couldn't put nginx's proxy key file back."
    fi
    dk 30 exec "$holder" rm -f "$socket_dir/mbtest-other" "$socket_dir/mbtest-other.sock" > /dev/null 2>&1 || true
    if [ "$(dk 30 container inspect -f '{{.State.Running}}' "$dashboard" 2> /dev/null)" = false ]; then
        start_dashboard_again || echo "The dashboard didn't start again within 90 seconds."
    fi
}

remove_bot() {   # the stand-in bot's containers, image tags and volumes, and the images revision_labels makes
    local tags tag
    dk 60 rm -f -v "$bot_container" "$stack-other" "$stack-labels" > /dev/null 2>&1 || true
    tags=$(dk 30 images --format '{{.Repository}}:{{.Tag}}' "$bot_image" 2> /dev/null) || tags=''
    for tag in $tags; do
        dk 60 rmi "$tag" > /dev/null 2>&1 || true
    done
    dk 30 volume rm "$bot_volume" "$stack-dashboard-keys" > /dev/null 2>&1 || true
}

check_cleanup() {
    put_back
    remove_bot
}

image_settings() {
    local health env user want
    health=$(dk 30 image inspect -f '{{json .Config.Healthcheck}}' "$image" 2>&1)
    for want in '"Test":["CMD","dotnet","MinerBot.Dashboard.dll","healthcheck"]' '"Interval":30000000000' \
        '"Timeout":10000000000' '"StartPeriod":30000000000' '"StartInterval":2000000000' '"Retries":3'; do
        expect "$want" "$health" "the image's health check"
    done
    env=$(dk 30 image inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$image" 2>&1)
    grep -qx 'DOTNET_EnableDiagnostics=0' <<< "$env" || problem "the image's environment has no DOTNET_EnableDiagnostics=0" "$env"
    user=$(dk 30 image inspect -f '{{.Config.User}}' "$image" 2>&1)
    [ "$user" = 1654 ] || problem "the image's user is '$user', not 1654"
}

database() {
    local out at_127 at_localhost
    out=$(sql -N <<< "SELECT @@character_set_server; SELECT CONCAT(version, ' ', name) FROM \`$bot_db\`.schema_version;" 2>&1)
    [ "$(printf '%s\n' "$out" | sed -n 1p)" = latin1 ] || problem "MariaDB's default character set isn't latin1" "$out"
    [ "$(printf '%s\n' "$out" | sed -n '2,$p')" = "1 0001_core" ] || problem "schema_version isn't exactly 1 0001_core" "$out"
    expect "rehearsed, applied and compared" "$(cat "$state/logs/rights.log" 2>&1)" "up's apply_rights"
    at_127=$(in_database "rights_of '$dashboard_login' 127.0.0.1" 2>&1) || problem "rights_of failed" "$at_127"
    at_localhost=$(in_database "rights_of '$dashboard_login' localhost" 2>&1) || problem "rights_of failed" "$at_localhost"
    [ "$at_127" = "$at_localhost" ] || problem "the login's rights differ between its two hosts" "$at_127"$'\n'"$at_localhost"
    expect "limits 15 20.000000 0 0 0" "$at_127" "the login's limits"
    expect "column SELECT $site_db ihvph_discord_servers guild_id" "$at_127" "the login's one right in the website's database"
    expect "table INSERT $bot_db audit_log" "$at_127" "the login's rights in MinerBot's database"
}

dashboard_health() {
    local health out logs listening
    health=$(dk 30 container inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$dashboard" 2>&1)
    [ "$health" = healthy ] || problem "Docker's health check says '$health'"
    out=$(dk 30 exec "$dashboard" dotnet MinerBot.Dashboard.dll healthcheck 2>&1) || problem "the healthcheck command failed" "$out"
    [ "$out" = Healthy ] || problem "the healthcheck command didn't say just Healthy" "$out"
    logs=$(dk 60 logs "$dashboard" 2>&1)
    expect "The Cloudflare Access check is off" "$logs" "the dashboard's start-up log"
    # Each start logs every address it listens on, one line each
    listening=$(grep -o 'Now listening on: .*' <<< "$logs" | tr -d '\r' | LC_ALL=C sort -u)
    [ "$listening" = "Now listening on: http://unix:$dashboard_socket" ] \
        || problem "the dashboard's log doesn't say it listens only on http://unix:$dashboard_socket" "$listening"
}

home_page() {
    local answer
    answer=$(from_cloudflare -H 'Accept-Encoding: gzip, br' -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 200" "$answer" "/"
    [ "$(header_count Content-Security-Policy "$answer")" = 1 ] || problem "Content-Security-Policy isn't there exactly once" "$answer"
    [ "$(header_count X-Frame-Options "$answer")" = 1 ] || problem "X-Frame-Options isn't there exactly once" "$answer"
    expect "default-src 'none'" "$(header_value Content-Security-Policy "$answer")" "the app's CSP"
    expect "DENY" "$(header_value X-Frame-Options "$answer")" "X-Frame-Options"
    expect "no-transform" "$(header_value Cache-Control "$answer")" "Cache-Control"
    [ "$(header_count Content-Encoding "$answer")" = 0 ] || problem "the page left nginx compressed" "$answer"
}

admin_page() {
    local answer location
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/admin")
    expect "HTTP/1.1 302" "$answer" "/admin"
    location=$(header_value Location "$answer")
    # https and the public name, from nginx's X-Forwarded-Proto and fixed Host, and the app's own parameter name
    [ "$location" = "https://$public/signin?returnUrl=%2Fadmin" ] || problem "/admin's redirect goes to '$location'"
}

signin_page() {
    local answer location state_value
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/signin")
    expect "HTTP/1.1 302" "$answer" "/signin"
    location=$(header_value Location "$answer")
    [[ $location == "https://discord.com/oauth2/authorize?"* ]] || problem "/signin's redirect goes to '${location:0:80}'"
    expect "client_id=$client_id" "$location" "the redirect to Discord"
    expect "redirect_uri=https%3A%2F%2F$public%2Fsignin-discord" "$location" "the redirect to Discord"
    expect "code_challenge_method=S256" "$location" "the redirect to Discord"
    state_value=${location#*state=}
    state_value=${state_value%%&*}
    # About 300 characters: the protected sign-in state, with the PKCE verifier in it
    [ ${#state_value} -ge 200 ] || problem "the state is only ${#state_value} characters"
    expect "Set-Cookie: __Host-mb-signin." "$answer" "the sign-in's correlation cookie"
    signin_note="the state is ${#state_value} characters and the headers $(printf '%s' "$answer" | wc -c | tr -d ' ') bytes"
}

outsider_refused() {
    local answer
    answer=$(request "$elsewhere" -H "CF-Connecting-IP: $visitor" -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 403" "$answer" "from $elsewhere_ip on 443"
    answer=$(request "$elsewhere" -H "CF-Connecting-IP: $visitor" -o /dev/null -D - "http://$public/")
    expect "HTTP/1.1 403" "$answer" "from $elsewhere_ip on 80"
}

# The dashboard's own answers on its socket, asked as root, without nginx. Its old port answers nothing now.
on_the_socket() {
    local said
    said=$(on_socket -o /dev/null -w '%{http_code}' -H "Host: $public" http://localhost/)
    [ "$said" = 404 ] || problem "the public name without the key got $said, not 404"
    said=$(on_socket -o /dev/null -w '%{http_code}' -H 'Host: localhost' http://localhost/)
    [ "$said" = 404 ] || problem "localhost's / without the key got $said, not 404"
    said=$(on_socket_with_key -o /dev/null -w '%{http_code}' -H 'Host: other.example' http://localhost/)
    [ "$said" = 400 ] || problem "another Host, even with the key, got $said, not 400"
    said=$(on_socket_with_key -o /dev/null -w '%{http_code}' -H "Host: $public" http://localhost/)
    [ "$said" = 200 ] || problem "the public name with nginx's key got $said, not 200"
    said=$(on_socket -H 'Host: localhost' http://localhost/health/live)
    [ "$said" = Healthy ] || problem "localhost's /health/live said '$said'"
    said=$(on_socket -H 'Host: localhost' http://localhost/health/ready)
    [ "$said" = Healthy ] || problem "localhost's /health/ready said '$said'"
    said=$(loopback -o /dev/null -w '%{http_code}' -H 'Host: localhost' http://127.0.0.1:5080/health/live)
    [ "$said" = 000 ] || problem "127.0.0.1:5080, where the dashboard listened before, answered $said"
}

# ---------- the dashboard's socket

# The mode, owner, group and kind of a path, as root on the server sees it, such as "2750 1654 65534 nobody directory",
# or nothing when it isn't there. Alpine names the group 65534 nobody, as the server does.
socket_stat() {   # socket_stat <path>
    dk 20 exec "$holder" stat -c '%a %u %g %G %F' "$1" 2> /dev/null || true
}

socket_folder_names() {   # what's in the socket's folder, one name per line
    dk 20 exec "$holder" ls -A "$socket_dir" 2>&1 || true
}

# Each nginx worker's real user and group IDs, one worker per line, such as "65534 65534"
nginx_worker_ids() {
    # shellcheck disable=SC2016 # the $ are for the container's shell
    dk 30 exec "$nginx" sh -c 'for worker in $(pgrep -P "$(cat /run/nginx.pid)"); do
            awk "/^Uid:/ { uid = \$2 } /^Gid:/ { gid = \$2 } END { print uid, gid }" "/proc/$worker/status"
        done' 2>&1 || true
}

who_is() {   # who_is <uid:gid>: the account, as the messages name it
    if [ "$1" = "$nobody_account" ]; then
        echo "nobody (65534), nginx's workers' account"
    else
        echo "another account (${1%%:*})"
    fi
}

# Has an account try to make a socket at the path, here with nginx, as any program of its own could: it prints what
# nginx said. The folder refuses at once with "(13: Permission denied)". A socket nginx can make, it listens on until
# the 5-second limit, and deletes as it stops.
bind_as() {   # bind_as <uid:gid> <socket path>
    local conf
    conf=$(printf '%s\n' 'pid nginx.pid;' 'error_log stderr notice;' 'events {}' 'http {' '    access_log off;' \
        '    client_body_temp_path body;' '    proxy_temp_path proxy;' '    fastcgi_temp_path fastcgi;' \
        '    uwsgi_temp_path uwsgi;' '    scgi_temp_path scgi;' "    server { listen unix:$2; }" '}')
    # shellcheck disable=SC2016 # the $ are for the container's shell
    dk 30 exec -i -u "$1" "$holder" sh -c 'folder=$(mktemp -d) || exit 1
        cat > "$folder/nginx.conf" || exit 1
        timeout 5 nginx -e stderr -p "$folder/" -c "$folder/nginx.conf" -g "daemon off;" 2>&1
        status=$?
        rm -rf "$folder"
        exit "$status"' <<< "$conf" 2>&1 || true
}

# deploy.sh's readiness check on the socket, in the server's OS as root: common.sh's own socket_problem, which status.sh
# uses too, then /health/ready through the socket, read up to 64 bytes. It prints what socket_problem said, or "none",
# and on the next line what /health/ready answered.
server_rule() {
    local out
    have_image "$alma_image" || {
        echo "couldn't pull $alma_image within 5 minutes"
        return 0
    }
    dk 30 rm -f -v "$stack-rule" > /dev/null 2>&1 || true
    # shellcheck disable=SC2016 # the $ are for the container's shell
    out=$(dk 120 run -i --rm --name "$stack-rule" --label "$label" --network none "${socket_mount_ro[@]}" "$alma_image" \
        bash -c 'cat > /tmp/common.sh && source /tmp/common.sh || exit 1
            why=$(socket_problem)
            said=$(run_for 10 curl -s --max-time 5 --unix-socket "$dashboard_socket" -H "Host: localhost" \
                http://localhost/health/ready | head -c 64)
            printf "%s\n%s\n" "${why:-none}" "$said"' < "$deploy/common.sh" 2>&1) || out="it didn't run: $out"
    dk 30 rm -f -v "$stack-rule" > /dev/null 2>&1 || true
    printf '%s\n' "$out"
}

# The socket's folder and the socket as on the server: the folder 2750, owned by 1654 and nobody, with nothing in it but
# the socket, which the dashboard owns, nobody's group can read and write, and no one else can open. nginx's workers run
# as nobody. deploy.sh's own readiness rule passes. compose.yml, nginx's file and the tmpfiles line name the same paths as
# common.sh, which this stack uses.
socket_files() {
    local said ids compose conf tmpfiles
    said=$(socket_stat "$socket_dir")
    [ "$said" = "2750 1654 65534 nobody directory" ] \
        || problem "the socket's folder is '$said', not '2750 1654 65534 nobody directory' (mode, owner, group, its name, kind)"
    said=$(socket_stat "$dashboard_socket")
    [ "$said" = "660 1654 65534 nobody socket" ] \
        || problem "the dashboard's socket is '$said', not '660 1654 65534 nobody socket' (mode, owner, group, its name, kind)"
    said=$(socket_folder_names)
    [ "$said" = dashboard.sock ] || problem "the socket's folder holds more or less than dashboard.sock" "$said"
    ids=$(nginx_worker_ids)
    if [ -z "$ids" ] || grep -qvx '65534 65534' <<< "$ids"; then
        problem "nginx's workers don't all run as 65534:65534, nobody" "$ids"
    fi
    said=$(server_rule)
    [ "$said" = $'none\nHealthy' ] \
        || problem "deploy.sh's readiness rule in $alma_image, socket_problem and then /health/ready, didn't pass" "$said"
    compose=$(tr -d '\r' < "$deploy/compose.yml")
    conf=$(tr -d '\r' < "$deploy/minerbot-dashboard.conf")
    tmpfiles=$(tr -d '\r' < "$deploy/minerbot-dashboard.tmpfiles")
    # Each line by its words, whatever the spaces around them
    said=$(awk -v want="$dashboard_socket" '$1 == "Dashboard__UnixSocket:" && $2 == want && NF == 2 { n++ } END { print n + 0 }' \
        <<< "$compose")
    [ "$said" = 1 ] || problem "compose.yml doesn't give the dashboard Dashboard__UnixSocket: $dashboard_socket once"
    for said in "source: $socket_dir" "target: $socket_dir" "create_host_path: false"; do
        if [ "$(awk -v want="$said" '$1 " " $2 == want && NF == 2 { n++ } END { print n + 0 }' <<< "$compose")" != 1 ]; then
            problem "compose.yml doesn't have the line '$said' once, for the socket's folder"
        fi
    done
    said=$(awk -v want="http://unix:$dashboard_socket;" '$1 == "proxy_pass" && $2 == want && NF == 2 { n++ } END { print n + 0 }' \
        <<< "$conf")
    [ "$said" = 2 ] || problem "nginx's file has proxy_pass http://unix:$dashboard_socket; $said time(s), not 2"
    said=$(grep -c 'proxy_pass' <<< "$conf" || true)
    [ "$said" = 2 ] || problem "nginx's file has $said proxy_pass lines, not just the two to the socket"
    said=$(grep -cxF "d $socket_dir 2750 1654 nobody -" <<< "$tmpfiles" || true)
    [ "$said" = 1 ] || problem "deploy/minerbot-dashboard.tmpfiles doesn't have the line 'd $socket_dir 2750 1654 nobody -'"
}

# Who can reach the socket, and who can make a file in its folder, as the server's other accounts would try: nobody's
# group can ask the dashboard, which still answers the bare 404 without nginx's key; any other account can't even
# enter the folder; and neither can make a file or a socket there.
socket_access() {
    local said account out
    said=$(on_socket_as "$nobody_account" -H 'Host: localhost' http://localhost/health/live)
    [ "$said" = Healthy ] || problem "$(who_is "$nobody_account") couldn't ask /health/live through the socket" "$said"
    said=$(on_socket_as "$nobody_account" -o /dev/null -w '%{http_code}' -H "Host: $public" http://localhost/)
    [ "$said" = 404 ] || problem "$(who_is "$nobody_account") got $said for the public name without the key, not 404"
    said=$(on_socket_as "$other_account" -o /dev/null -w '%{http_code}' -H 'Host: localhost' http://localhost/health/live)
    [ "$said" = 000 ] || problem "$(who_is "$other_account") reached the dashboard through the socket: $said"
    for account in "$other_account" "$nobody_account"; do
        if out=$(dk 20 exec -u "$account" "$holder" touch "$socket_dir/mbtest-other" 2>&1); then
            problem "$(who_is "$account") made a file in the socket's folder"
        fi
        expect "Permission denied" "$out" "$(who_is "$account") making a file in the socket's folder"
        out=$(bind_as "$account" "$socket_dir/mbtest-other.sock")
        expect "bind() to unix:$socket_dir/mbtest-other.sock failed (13: Permission denied)" "$out" \
            "$(who_is "$account") making a socket in the socket's folder"
    done
    said=$(socket_folder_names)
    [ "$said" = dashboard.sock ] || problem "the socket's folder holds more than dashboard.sock afterwards" "$said"
    dk 30 exec "$holder" rm -f "$socket_dir/mbtest-other" "$socket_dir/mbtest-other.sock" > /dev/null 2>&1 || true
}

# The TCP ports anything listens on in the stack's network, in decimal, one per line and sorted, from the kernel's own
# tables, which every container of the stack shares. Docker's DNS server for the network, on 127.0.0.11, is left out.
tcp_listeners() {
    local table hex
    table=$(dk 20 exec "$holder" sh -c 'cat /proc/net/tcp; cat /proc/net/tcp6 2> /dev/null; true' 2>&1) || return 1
    awk '$4 == "0A" { split($2, address, ":"); if (address[1] != "0B00007F") print address[2] }' <<< "$table" \
        | while IFS= read -r -t 5 hex; do printf '%d\n' "0x$hex"; done | LC_ALL=C sort -nu
}

error_lines() {   # how many lines nginx's error log for the dashboard has now
    local count
    count=$(dk 30 exec "$nginx" sh -c 'cat /var/log/nginx/minerbot-dashboard.error.log 2> /dev/null | wc -l' 2> /dev/null) \
        || count=0
    count=${count//[!0-9]/}
    printf '%s\n' "${count:-0}"
}

errors_since() {   # errors_since <line count>: nginx's error log for the dashboard after that many lines
    dk 30 exec "$nginx" tail -n "+$(($1 + 1))" /var/log/nginx/minerbot-dashboard.error.log 2>&1 || true
}

dashboard_stopped() {
    [ "$(dk 30 container inspect -f '{{.State.Running}}' "$dashboard" 2> /dev/null)" = false ]
}

broken_settings() {
    local out status=0 key value
    local bad_key=shortkey4mbtest bad_id=98765mbtest bad_team=mbtest-team.cloudflareaccess.com
    {
        printf 'Dashboard__PublicOrigin=https://%s\n' "$public"
        printf 'Dashboard__ProxyKey=%s\n' "$bad_key"
        printf 'Dashboard__Owners=%s\n' "$owner_id"
        printf 'Dashboard__Admins=%s\n' "$owner_id"
        printf 'Discord__ClientId=%s\n' "$bad_id"
        printf 'Discord__ClientSecret=%s\n' "$client_secret"
        printf 'CloudflareAccess__TeamDomain=%s\n' "$bad_team"
        printf 'CloudflareAccess__Audience=\n'
        printf 'ConnectionStrings__DefaultConnection=Server=127.0.0.1;Port=3306;Uid=%s;Pwd=%s;SslMode=Disabled;\n' \
            "$dashboard_login" "$db_password"
        printf 'ConnectionStrings__BotConnection=%s\n' "$(connection "$bot_db" "$dashboard_login" "$db_password")"
    } > "$state/broken.env"
    out=$(check_settings_on "$state/broken.env" 2>&1) || status=$?
    printf '%s\n' "$out" > "$state/logs/check-settings-broken.log"
    [ "$status" = 1 ] || problem "check-settings exited $status, not 1" "$out"
    [ "$(printf '%s\n' "$out" | sed -n 1p)" = "The dashboard didn't start, because of its settings:" ] \
        || problem "check-settings' first line isn't the usual header" "$out"
    for key in Dashboard:ProxyKey Dashboard:Admins Discord:ClientId CloudflareAccess:TeamDomain \
        ConnectionStrings:DefaultConnection; do
        expect "$key" "$out" "check-settings' list"
    done
    for value in "$bad_key" "$bad_id" "$bad_team" "$owner_id" "$client_secret" "$db_password"; do
        if grep -qF -- "$value" <<< "$out"; then problem "check-settings' list repeats a value it was given"; fi
    done
}

key_missing() {
    local out answer
    out=$(dk 30 exec "$nginx" mv /etc/nginx/minerbot/proxy-key.conf /etc/nginx/minerbot/proxy-key.off 2>&1) || {
        problem "couldn't rename nginx's key file" "$out"
        return
    }
    out=$(reload_nginx 2>&1) || problem "nginx didn't reload without the key file" "$out"
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 404" "$answer" "/ while nginx has no key file"
    out=$(put_key_back 2>&1) || problem "couldn't put nginx's key file back" "$out"
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 200" "$answer" "/ with the key file back"
}

# A clean stop, as a deploy's swap makes: the dashboard deletes its socket, nginx answers with its 503 page, and while it
# stays down no other account can put a socket in its place. Its TCP ports are the same with it and without it: none.
dashboard_down() {
    local out answer before with without account said
    with=$(tcp_listeners) || with=''
    before=$(error_lines)
    out=$(dk 60 stop -t 30 "$dashboard" 2>&1) || {
        problem "couldn't stop the dashboard" "$out"
        return
    }
    said=$(socket_stat "$dashboard_socket")
    [ -z "$said" ] || problem "the socket is still there after a clean stop: $said"
    answer=$(from_cloudflare "https://$public/admin")
    expect "HTTP/1.1 503" "$answer" "the dashboard stopped"
    expect "Retry-After: 30" "$answer" "the dashboard stopped"
    expect "Cache-Control: no-store" "$answer" "the dashboard stopped"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "the dashboard stopped"
    expect "connect() to unix:$dashboard_socket failed (2: No such file or directory)" "$(errors_since "$before")" \
        "nginx's error log, with the socket gone"
    said=$(socket_stat "$socket_dir")
    [ "$said" = "2750 1654 65534 nobody directory" ] \
        || problem "with the dashboard stopped, the socket's folder is '$said', not '2750 1654 65534 nobody directory'"
    for account in "$other_account" "$nobody_account"; do
        out=$(bind_as "$account" "$dashboard_socket")
        expect "bind() to unix:$dashboard_socket failed (13: Permission denied)" "$out" \
            "$(who_is "$account") putting a socket in the dashboard's place while it's stopped"
    done
    said=$(socket_folder_names)
    [ -z "$said" ] || problem "the socket's folder isn't empty while the dashboard is stopped" "$said"
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 503" "$answer" "/ after other accounts tried the socket's place"
    without=$(tcp_listeners) || without=''
    if ! grep -qx 443 <<< "$with" || ! grep -qx 3306 <<< "$with"; then
        problem "the TCP listeners read don't have nginx's 443 and MariaDB's 3306, so they weren't read right" "$with"
    elif [ "$with" != "$without" ]; then
        problem "the dashboard's stop closed TCP listeners, so it listened on a port" \
            "with the dashboard: ${with//$'\n'/ }"$'\n'"without: ${without//$'\n'/ }"
    fi
    start_dashboard_again || problem "the dashboard wasn't ready within 90 seconds of starting again"
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 200" "$answer" "/ after the dashboard started again"
}

# What a dashboard killed outright leaves, by the memory limit or at the end of the stop's grace period: its socket, with
# nothing listening. nginx answers with its 503 page, and the next start deletes that socket and listens again.
dashboard_killed() {
    local out answer before started logs left
    out=$(dk 30 kill "$dashboard" 2>&1) || {
        problem "couldn't kill the dashboard" "$out"
        return
    }
    wait_for 30 dashboard_stopped || problem "the dashboard still ran 30 seconds after docker kill"
    left=$(socket_stat "$dashboard_socket")
    [ "$left" = "660 1654 65534 nobody socket" ] \
        || problem "docker kill left '$left' at the socket's path, not the dashboard's socket, so this check proves nothing"
    before=$(error_lines)
    answer=$(from_cloudflare "https://$public/admin")
    expect "HTTP/1.1 503" "$answer" "the dashboard killed"
    expect "Retry-After: 30" "$answer" "the dashboard killed"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "the dashboard killed"
    expect "connect() to unix:$dashboard_socket failed (111: Connection refused)" "$(errors_since "$before")" \
        "nginx's error log, with a socket nothing listens on"
    start_dashboard_again || problem "the dashboard wasn't ready within 90 seconds of starting again over the socket left behind"
    started=$(dk 30 container inspect -f '{{.State.StartedAt}}' "$dashboard" 2>&1) || started=''
    logs=$(dk 60 logs --since "$started" "$dashboard" 2>&1) || problem "couldn't read the dashboard's log" "$logs"
    expect "Deleted the socket at $dashboard_socket, which a dashboard that was stopped without closing it left behind" \
        "$logs" "the dashboard's log as it started again"
    left=$(socket_stat "$dashboard_socket")
    [ "$left" = "660 1654 65534 nobody socket" ] \
        || problem "after the start, the socket is '$left', not '660 1654 65534 nobody socket'"
    answer=$(from_cloudflare -o /dev/null -D - "https://$public/")
    expect "HTTP/1.1 200" "$answer" "/ after the dashboard started again over the socket left behind"
}

with_command() {   # the stand-in bot runs sleep instead of the bot
    awk '{ print } $0 == "    container_name: mbtest-stack-bot" { print "    command: [\"sleep\", \"infinity\"]" }'
}

bot_state() {   # the stand-in bot's container ID, start time and Compose's label, or nothing
    dk 30 container inspect -f '{{.Id}} {{.State.StartedAt}} {{index .Config.Labels "com.docker.compose.config-hash"}}' \
        "$bot_container" 2> /dev/null || true
}

bot_running() {
    running "$bot_container"
}

# The stand-in bot, made the way the server's bot was: with today's compose file, from deploy/tests/fixtures, with the
# names changed to mbtest-stack and sleep for a command, from a tag of the .NET runtime image. Sets bot_problem when it
# can't, which the bot's checks report.
make_bot() {
    local out runtime_id name
    bot_problem=''
    have_image "$runtime_image" || {
        bot_problem="couldn't pull $runtime_image within 5 minutes"
        return
    }
    rm -rf "$home"
    mkdir -p "$home" "$home/old"
    tr -d '\r' < "$here/fixtures/compose-before-pr3.yml" | sed 's/minerbot/mbtest-stack/g' | with_command > "$home/old/compose.yml"
    tr -d '\r' < "$deploy/compose.yml" | sed 's/minerbot/mbtest-stack/g' | with_command > "$home/compose.yml"
    for name in old/compose.yml compose.yml; do
        if [ "$(grep -c 'command: \["sleep", "infinity"\]' "$home/$name")" != 1 ]; then
            bot_problem="the bot's command didn't go into $name"
            return
        fi
    done
    printf 'MBTEST_SETTING=one\nMBTEST_OTHER=two\n' > "$home/bot.env"
    if ! runtime_id=$(image_id "$runtime_image") || [ -z "$runtime_id" ] \
        || ! (tag_id "$runtime_id" "$bot_image:current") > /dev/null 2>&1; then
        bot_problem="couldn't tag $runtime_image as $bot_image:current"
        return
    fi
    if ! out=$(run_for 120 docker compose -f "$home/old/compose.yml" --project-directory "$home" \
        up -d --no-build --no-deps bot 2>&1); then
        bot_problem="Compose didn't make the stand-in bot with today's file: ${out//$'\n'/ }"
        return
    fi
    wait_for 30 bot_running || bot_problem="the stand-in bot isn't running"
}

# bot_unchanged's answer for a compose file in dry_status: 0 when the bot would stay as it runs, 1 when Compose plans a
# change, 2 when the dry run failed, 3 for lines it doesn't know. Compose's lines are in dry_run.
dry_run_with() {   # dry_run_with <compose file>
    dry_status=0
    bot_unchanged "$1" || dry_status=$?
}

bot_dry_runs_pass() {
    local old new
    bot_before=$(bot_state)
    dry_run_with "$home/old/compose.yml"
    [ "$dry_status" = 0 ] || problem "bot_unchanged gave $dry_status, not 0, for today's compose file" "$dry_run"
    dry_run_with "$home/compose.yml"
    [ "$dry_status" = 0 ] || problem "bot_unchanged gave $dry_status, not 0, for the new compose file" "$dry_run"
    old=$(run_for 30 docker compose -f "$home/old/compose.yml" --project-directory "$home" config --hash bot 2>&1) \
        || problem "Compose couldn't hash today's file" "$old"
    new=$(run_for 30 docker compose -f "$home/compose.yml" --project-directory "$home" config --hash bot 2>&1) \
        || problem "Compose couldn't hash the new file" "$new"
    [ "$old" = "$new" ] || problem "config --hash bot differs between today's file and the new one" "$old"$'\n'"$new"
}

bot_dry_runs_refuse() {
    printf 'MBTEST_SETTING=changed\nMBTEST_OTHER=two\n' > "$home/bot.env"
    dry_run_with "$home/old/compose.yml"
    [ "$dry_status" = 1 ] || problem "bot_unchanged gave $dry_status, not 1, for today's file after bot.env changed" "$dry_run"
    expect Recreate "$dry_run" "the dry run with today's file after bot.env changed"
    dry_run_with "$home/compose.yml"
    [ "$dry_status" = 1 ] || problem "bot_unchanged gave $dry_status, not 1, for the new file after bot.env changed" "$dry_run"
    expect Recreate "$dry_run" "the dry run with the new file after bot.env changed"
    printf 'MBTEST_SETTING=one\nMBTEST_OTHER=two\n' > "$home/bot.env"
    sed 's/max-file: "5"/max-file: "6"/' "$home/compose.yml" > "$home/max-file.yml"
    dry_run_with "$home/max-file.yml"
    [ "$dry_status" = 1 ] || problem "bot_unchanged gave $dry_status, not 1, after a max-file change" "$dry_run"
    expect Recreate "$dry_run" "the dry run after a max-file change"
    dry_run_with "$home/compose.yml"
    [ "$dry_status" = 0 ] || problem "bot_unchanged gave $dry_status, not 0, for the new file once bot.env was as before" "$dry_run"
}

bot_untouched() {
    local after
    after=$(bot_state)
    if [ -z "$bot_before" ] || [ "$after" != "$bot_before" ]; then
        problem "the stand-in bot's container changed: before '$bot_before', after '$after'"
    fi
}

# The swap goes by image ID, and relies on Compose recreating a container when the ID behind :current changes, and only
# then. start_service is the swap's own command.
bot_swap() {
    local first_id other_id first second third out seen
    first_id=$(image_id "$bot_image:current") || first_id=''
    first=$(bot_state)
    out=$(start_service bot 2>&1) || problem "compose up failed with :current as it was" "$out"
    [ "$(bot_state)" = "$first" ] || problem "compose up recreated the bot although :current's ID was the same" "$out"
    seen=$(container_image "$bot_container") || seen=''
    if [ -z "$first_id" ] || [ "$seen" != "$first_id" ]; then
        problem "the container's image ID ($seen) isn't :current's ID ($first_id), so the swap's comparison can't work"
    fi
    # Another image ID, without a build: the runtime image with one more label, by docker commit
    if ! out=$(dk 60 create --name "$stack-other" --label "$label" "$runtime_image" true 2>&1) \
        || ! out=$(dk 60 commit --change "LABEL org.opencontainers.image.revision=mbtest-stack-other" "$stack-other" \
            "$bot_image:other" 2>&1); then
        problem "couldn't make a second image" "$out"
    fi
    dk 30 rm -f -v "$stack-other" > /dev/null 2>&1 || true
    other_id=$(image_id "$bot_image:other") || other_id=''
    if [ -z "$other_id" ] || [ "$other_id" = "$first_id" ]; then
        problem "the second image has no ID of its own"
        return
    fi
    (tag_id "$other_id" "$bot_image:current") > /dev/null 2>&1 || problem "couldn't tag the second image as :current"
    out=$(start_service bot 2>&1) || problem "compose up failed after :current moved" "$out"
    second=$(bot_state)
    [ "${second%% *}" != "${first%% *}" ] || problem "compose up left the bot as it was after :current moved to another ID" "$out"
    seen=$(container_image "$bot_container") || seen=''
    [ "$seen" = "$other_id" ] || problem "the recreated bot runs $seen, not the second image $other_id"
    wait_for 30 bot_running || problem "the recreated bot isn't running"
    (tag_id "$first_id" "$bot_image:current") > /dev/null 2>&1 || problem "couldn't tag the first image as :current again"
    out=$(start_service bot 2>&1) || problem "compose up failed after :current moved back" "$out"
    third=$(bot_state)
    [ "${third%% *}" != "${second%% *}" ] || problem "compose up didn't recreate the bot when :current moved back" "$out"
    seen=$(container_image "$bot_container") || seen=''
    [ "$seen" = "$first_id" ] || problem "the bot runs $seen after the move back, not $first_id"
    out=$(start_service bot 2>&1) || problem "a second compose up failed" "$out"
    [ "$(bot_state)" = "$third" ] || problem "a second compose up on the same ID recreated the bot" "$out"
}

# revision in common.sh, which names images in messages and the history, against real Docker's templates: an image with
# the revision label gives the label's first 12 characters, and one with labels but not that one gives "image <12>",
# where asking for the missing label would print "<no value>". Two images made by docker commit, without a build.
revision_labels() {
    local out said labels_id
    have_image "$runtime_image" || {
        problem "couldn't pull $runtime_image within 5 minutes"
        return
    }
    dk 30 rm -f -v "$stack-labels" > /dev/null 2>&1 || true
    if ! out=$(dk 60 create --name "$stack-labels" --label "$label" "$runtime_image" true 2>&1) \
        || ! out=$(dk 60 commit --change "LABEL org.opencontainers.image.revision=0123456789abcdef0123456789abcdef01234567" \
            "$stack-labels" "$bot_image:revision" 2>&1) \
        || ! out=$(dk 60 commit --change "LABEL mbtest-stack.note=no-revision" "$stack-labels" "$bot_image:labels" 2>&1); then
        problem "couldn't make the two images" "$out"
    fi
    dk 30 rm -f -v "$stack-labels" > /dev/null 2>&1 || true
    said=$(revision "$bot_image:revision")
    [ "$said" = 0123456789ab ] || problem "revision gave '$said' for an image with the revision label, not 0123456789ab"
    out=$(dk 30 image inspect -f '{{json .Config.Labels}}' "$bot_image:labels" 2>&1) || out=''
    if [[ $out != *'"mbtest-stack.note":"no-revision"'* ]] || [[ $out == *org.opencontainers.image.revision* ]]; then
        problem "the second image doesn't have labels without the revision label, so this check proves nothing" "$out"
    fi
    labels_id=$(image_id "$bot_image:labels") || labels_id=''
    labels_id=${labels_id#sha256:}
    said=$(revision "$bot_image:labels")
    if [ -z "$labels_id" ] || [ "$said" != "image ${labels_id:0:12}" ]; then
        problem "revision gave '$said' for an image with labels but not the revision label, not 'image ${labels_id:0:12}'"
    fi
    said=$(revision "$bot_image:missing")
    [ "$said" = none ] || problem "revision gave '$said' for an image that isn't there, not none"
}

no_secrets_in_logs() {
    local name text file i where
    local -a secrets=("$proxy_key" "$db_password" "$client_secret" "$bot_password" "$root_password")
    local -a what=("the proxy key" "the dashboard's database password" "the client secret" "the bot-like login's password"
        "MariaDB's root password")
    text=$(dk 30 exec "$nginx" cat /var/log/nginx/minerbot-dashboard.access.log 2>&1)
    expect '"GET / HTTP/1.1" 200' "$text" "nginx's access log for the dashboard"
    for name in "$holder" "$db" "$dashboard" "$nginx" "$cloudflare" "$elsewhere" nginx-files own-logs; do
        case $name in
            nginx-files)
                where="nginx's log files"
                text=$(dk 30 exec "$nginx" sh -c 'cat /var/log/nginx/*.log' 2>&1) || true
                ;;
            own-logs)
                where="this stack's own logs in $state"
                text=''
                for file in "$state"/logs/*.log "$state"/image/build.log; do
                    if [ -f "$file" ]; then text+=$(cat "$file")$'\n'; fi
                done
                ;;
            *)
                where="the docker logs of $name"
                text=$(dk 60 logs "$name" 2>&1) || problem "couldn't read the docker logs of $name" "$text"
                ;;
        esac
        for i in "${!secrets[@]}"; do
            if grep -qF -- "${secrets[$i]}" <<< "$text"; then problem "${what[$i]} is in $where"; fi
        done
    done
}

check() {
    local name left
    docker_answers
    load_secrets
    for name in "$holder" "$db" "$nginx" "$cloudflare" "$elsewhere"; do
        running "$name" || stop "$name isn't running. The stack stops by itself $lifetime seconds after up. Run down, then up."
    done
    dk 30 container inspect "$dashboard" > /dev/null 2>&1 || stop "There's no $dashboard. Run down, then up."
    checks=20
    made=0
    passed=0
    notes=''
    trap 'status=$?; check_cleanup || true; exit "$status"' EXIT
    put_back
    remove_bot

    image_settings || true
    verdict "the image has the health check every 2 s while it starts and every 30 s after, DOTNET_EnableDiagnostics=0, and user 1654"
    database || true
    verdict "MariaDB's default is latin1, the migrations ran with the bot-like login (schema_version 1 0001_core), and apply_rights left the same rights on both of the login's hosts"
    dashboard_health || true
    verdict "Docker's health check says healthy, the healthcheck command exits 0 with Healthy, and the start-up log says the Access check is off and that it listens only on its socket"
    home_page || true
    verdict "/ gives 200 through nginx, with the app's CSP and X-Frame-Options exactly once each, no-transform, and nothing compressed"
    admin_page || true
    verdict "/admin gives 302 to https://$public/signin?returnUrl=%2Fadmin"
    signin_note='no answer'
    signin_page || true
    verdict "/signin gives 302 to discord.com with the long state, through nginx's 16k proxy buffer: $signin_note"
    outsider_refused || true
    verdict "$elsewhere_ip, outside the stand-in Cloudflare list, gets 403 on 443 and on 80"
    on_the_socket || true
    verdict "on the dashboard's socket, as root: 404 without the key, 400 for another Host even with it, 200 with it, Healthy from localhost's /health/live and /health/ready; and nothing answers on 127.0.0.1:5080"
    socket_files || true
    verdict "the socket's folder is 2750, 1654:nobody, with only the socket in it, which is 660, 1654:nobody; nginx's workers run as nobody; deploy.sh's readiness rule passes in $alma_image; compose.yml, nginx's file and the tmpfiles line name the same paths"
    socket_access || true
    verdict "nobody, nginx's account, can ask the dashboard through the socket and gets the bare 404 without the key; another account can't reach it; neither can make a file or a socket in its folder"
    broken_settings || true
    verdict "check-settings with a broken env exits 1 and names each setting, never a value"
    key_missing || true
    verdict "with nginx's proxy key file renamed, the dashboard's 404 comes through nginx, and with it back, 200"
    dashboard_down || true
    verdict "with the dashboard stopped, its socket is gone and nginx gives its 503 page with Retry-After: 30; no other account can put a socket in its place; it had no TCP port; and once it's started again, 200"
    dashboard_killed || true
    verdict "with the dashboard killed, the socket it left gives nginx's 503 page; the next start deletes it, says so, and listens again, 660, 1654:nobody, and / gives 200"

    make_bot
    if [ -n "$bot_problem" ]; then problem "$bot_problem"; else bot_dry_runs_pass || true; fi
    verdict "bot_unchanged gives 0 for today's compose file and for the new one, and config --hash bot is the same for both"
    if [ -n "$bot_problem" ]; then problem "$bot_problem"; else bot_dry_runs_refuse || true; fi
    verdict "bot_unchanged gives 1 after a line of bot.env changes and after a max-file change, where Compose would recreate"
    if [ -n "$bot_problem" ]; then problem "$bot_problem"; else bot_untouched || true; fi
    verdict "after every dry run, the stand-in bot has the same container ID, start time and Compose label"
    if [ -n "$bot_problem" ]; then problem "$bot_problem"; else bot_swap || true; fi
    verdict "compose up leaves the bot when :current has the same image ID, and recreates it on the other ID when :current moves"
    revision_labels || true
    verdict "revision names an image with the revision label by its first 12 characters, one with labels but not that one as 'image <12>', and a missing one none"
    remove_bot
    left=$(image_leftovers)
    [ -z "$left" ] || echo "The stand-in bot's images are still there: $left"

    no_secrets_in_logs || true
    verdict "no proxy key, database password or client secret in any container's docker logs, nginx's logs, or this stack's own logs"

    echo "$passed of $checks passed"
    if [ "$made" != "$checks" ]; then
        echo "This script made $made checks, but checks= in check says $checks. Correct that count."
        exit 1
    fi
    [ "$passed" = "$checks" ] || exit 1
}

# ---------- down

cache_left() {   # which of the build cache records up's build made are still there, one per line
    local ids
    ids=$(dk 60 buildx du --format '{{.ID}}' 2> /dev/null) || {
        say "docker buildx du didn't answer, so the build cache up's build made may still be there." >&2
        return 1
    }
    grep -xFf "$state/image/cache-ids" <<< "$ids" || true
}

# The build cache records up's build made, by their exact IDs. A record another one was built on stays until that one
# is gone, so it goes round again while that removes more, at most five times.
remove_build_cache() {
    local id left before
    if [ ! -s "$state/image/cache-ids" ]; then
        if [ "$(cat "$state/image/built" 2> /dev/null)" != yes ]; then
            say "up didn't build $image, so the build cache its build made isn't known here, and is left as it is."
        fi
        return 0
    fi
    left=$(cache_left) || return 1
    for _ in 1 2 3 4 5; do
        [ -n "$left" ] || break
        for id in $left; do
            [[ $id =~ ^[a-z0-9]+$ ]] || continue
            dk 120 buildx prune -f --all --filter "id=^$id\$" > /dev/null 2>&1 || true
        done
        before=$left
        left=$(cache_left) || return 1
        [ "$left" != "$before" ] || break
    done
    if [ -n "$left" ]; then
        say "$(wc -l <<< "$left" | tr -d ' ') of the build cache records up's build made are still there; docker buildx du lists them."
        return 1
    fi
    say "Removed the $(wc -l < "$state/image/cache-ids" | tr -d ' ') build cache records up's build made."
}

anonymous_volumes() {   # the anonymous volumes up's containers made, which it noted as it went
    if [ -f "$state/volumes" ]; then
        awk 'NF' "$state/volumes"
    fi
}

down() {
    local images=no left pulled name volume trouble=0
    case ${1:-} in
        '') ;;
        --images) images=yes ;;
        *) usage ;;
    esac
    [ $# -le 1 ] || usage
    docker_answers
    remove_bot
    for _ in 1 2 3 4 5; do
        dk 120 rm -f -v "$cloudflare" "$elsewhere" "$nginx" "$dashboard" "$db" "$stack-migrate" "$stack-check" \
            "$stack-rule" "$stack-other" "$stack-labels" "$bot_container" "$holder" > /dev/null 2>&1 || true
        # Anything else with the stack's label or name, in case a run was cut while it made one
        for name in $(dk 30 ps -aq --filter "label=$stack" 2> /dev/null || true) \
            $(dk 30 ps -aq --filter "name=$stack-" 2> /dev/null || true); do
            dk 60 rm -f -v "$name" > /dev/null 2>&1 || true
        done
        for volume in "$keys_volume" "$socket_volume" "$bot_volume" "$stack-dashboard-keys" $(anonymous_volumes); do
            dk 60 volume rm "$volume" > /dev/null 2>&1 || true
        done
        dk 60 network rm "$net" > /dev/null 2>&1 || true
        left=$(leftovers)
        [ -n "$left" ] || break
        sleep 1
    done
    for volume in $(anonymous_volumes); do
        if dk 30 volume inspect "$volume" > /dev/null 2>&1; then left+=" $volume"; fi
    done
    if [ -n "$left" ]; then
        say "These are still there: $left. Remove them with docker rm -f -v, docker volume rm and docker network rm."
        trouble=1
    fi
    if [ "$images" = yes ]; then
        if dk 30 image inspect "$image" > /dev/null 2>&1; then
            if dk 120 rmi "$image" > /dev/null; then say "Removed $image."; else trouble=1; fi
        fi
        remove_build_cache || trouble=1
        if [ -f "$state/image/pulled" ]; then
            while IFS= read -r -t 5 pulled; do
                [ -n "$pulled" ] || continue
                if dk 120 rmi "$pulled" > /dev/null; then say "Removed $pulled, which this stack pulled."; else trouble=1; fi
            done < "$state/image/pulled"
        fi
    fi
    left=$(image_leftovers "${1:-}")
    if [ -n "$left" ]; then
        say "These images are still there: $left. Remove them with docker rmi."
        trouble=1
    fi
    # The files go only once nothing is left, so another down can find what this one couldn't remove
    [ "$trouble" = 0 ] || exit 1
    if [ "$images" = yes ]; then
        rm -rf "$state"
    elif [ -d "$state" ]; then
        find "$state" -mindepth 1 -maxdepth 1 ! -name image -exec rm -rf {} +
    fi
    if [ "$images" = yes ]; then
        say "Nothing of the stack is left: no container, network, volume, image or file of its own."
    elif dk 30 image inspect "$image" > /dev/null 2>&1; then
        say "Nothing of the stack is left but the image $image, which down --images removes with its build cache."
    else
        say "Nothing of the stack is left."
    fi
}

trap 'exit 130' INT
trap 'exit 143' TERM
trap 'exit 129' HUP

case ${1:-} in
    up)
        [ $# = 1 ] || usage
        up
        ;;
    check)
        [ $# = 1 ] || usage
        check
        ;;
    down)
        shift
        down "$@"
        ;;
    *) usage ;;
esac

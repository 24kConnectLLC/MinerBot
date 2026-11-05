#!/usr/bin/env bash
# Checks deploy/minerbot-dashboard.conf in nginx's own image, at the server's nginx version, before it can reach the
# server, where a broken file would stop nginx from reloading for every site. nginx gets the file next to a default
# server and an account block like cPanel's, an http level that turns gzip on and adds a header, as cPanel's could, and
# a stand-in dashboard on 127.0.0.1:5080. Two clients send the requests: one from an address in the stand-in Cloudflare
# list, and one from elsewhere.
#
# The file must have the line "pipelog off;" exactly twice. The tested copy leaves those two lines out, because cPanel's
# pipelog module isn't in nginx's image.
#
# Run it from Git Bash or Linux: bash deploy/tests/nginx-test.sh. It needs Docker and openssl. It prints one line per
# check, then "N of M passed", and exits 1 if any check failed. To try a deliberate break, point
# MINERBOT_TEST_NGINX_CONF at a changed copy of the file, and the run tests that copy instead.
#
# Everything it makes in Docker is named mbtest-nginx-<its PID>-... and removed when it ends. Each container's main
# process is sleep 600, so even a run killed before it can clean up leaves nothing running after 10 minutes.
set -Eeuo pipefail
umask 077
# Git Bash would otherwise turn the container paths and openssl's -subj below into Windows paths
export MSYS_NO_PATHCONV=1

here=$(cd "$(dirname "$0")" && pwd)
conf=${MINERBOT_TEST_NGINX_CONF:-$(cd "$here/.." && pwd)/minerbot-dashboard.conf}
# The server's nginx version. Change it when the server's changes.
image=nginx:1.31.8-alpine
prefix=mbtest-nginx-$$
net=$prefix-net
nginx=$prefix-nginx
cloudflare=$prefix-cloudflare
elsewhere=$prefix-elsewhere
# No other test script uses this range
subnet=172.31.251.0/24
nginx_ip=172.31.251.10
cloudflare_ip=172.31.251.20
elsewhere_ip=172.31.251.30
# The visitor's address, as Cloudflare's CF-Connecting-IP header gives it
visitor=203.0.113.7
key=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef   # made up
# A line that is exactly "pipelog off;", give or take spaces
pipelog_line='[[:space:]]*pipelog off;[[:space:]]*'
checks=16   # how many checks this script makes
made=0
passed=0
notes=""
started=""
work=""

cleanup() {
    local left=""
    for _ in 1 2 3 4 5; do
        timeout -k 10 60 docker rm -f -v "$nginx" "$cloudflare" "$elsewhere" > /dev/null 2>&1 || true
        timeout -k 10 60 docker network rm "$net" > /dev/null 2>&1 || true
        left=$( {
            timeout -k 10 30 docker ps -aq --filter "name=$prefix-"
            timeout -k 10 30 docker network ls -q --filter "name=$prefix-"
        } 2> /dev/null || true)
        if [ -z "$left" ]; then break; fi
        sleep 1
    done
    if [ -n "$left" ]; then
        echo "Some of this run's containers, or its network, are still there. Remove them with:"
        echo "    docker rm -f $nginx $cloudflare $elsewhere; docker network rm $net"
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

run_container() {   # run_container <name> <address> [<docker run options...>]: one that ends by itself after 10 minutes
    local name=$1 address=$2 out
    shift 2
    out=$(timeout -k 10 120 docker run -d --rm --name "$name" --network "$net" --ip "$address" "$@" "$image" sleep 600 2>&1) \
        || stop "Couldn't start the container $name: $out"
}
inside() {   # inside <shell command>: runs it in nginx's container
    timeout -k 10 30 docker exec "$nginx" sh -c "$1"
}
request() {   # request <client> <curl arguments...>: nginx's answer, headers first, or curl's error
    local client=$1
    shift
    timeout -k 10 30 docker exec "$client" curl -sSk -i --max-time 10 \
        --resolve "bot.murderminershub.com:443:$nginx_ip" --resolve "bot.murderminershub.com:80:$nginx_ip" \
        --resolve "other.example:443:$nginx_ip" --resolve "unknown.example:443:$nginx_ip" "$@" 2>&1 || true
}
from_cloudflare() {   # from_cloudflare <curl arguments...>: a visitor's request, as Cloudflare passes it on
    request "$cloudflare" -H "CF-Connecting-IP: $visitor" "$@"
}
post() {   # post <bytes>: a body of that size from a visitor, through Cloudflare
    timeout -k 10 30 docker exec "$cloudflare" sh -c "head -c $1 /dev/zero | curl -sSk -i --max-time 10 \
        --resolve bot.murderminershub.com:443:$nginx_ip -H 'CF-Connecting-IP: $visitor' --data-binary @- \
        https://bot.murderminershub.com/form" 2>&1 || true
}
reload() {   # reload: has nginx read its files again, and waits up to 10 s until no worker runs the old ones
    # shellcheck disable=SC2016 # the $ are for the container's shell
    inside 'old=$(pgrep -P "$(cat /run/nginx.pid)") && nginx -s reload 2>&1 || exit 1
        for i in $(seq 100); do
            left=""
            for worker in $old; do if [ -d "/proc/$worker" ]; then left=$worker; fi; done
            if [ -z "$left" ]; then exit 0; fi
            sleep 0.1
        done
        echo "a worker with the old files still ran 10 seconds after the reload"
        exit 1'
}

# ---------- the checks, in the order they run

pipelog_twice() {
    local count
    count=$(grep -cxE "$pipelog_line" "$conf" || true)
    if [ "$count" != 2 ]; then problem "the line is there $count time(s), not 2"; fi
}

nginx_starts() {
    local out
    if ! out=$(inside 'nginx -t -q 2>&1'); then
        problem "nginx -t refused the files" "$out"
        return
    fi
    # nginx starts its master in the background once its ports are open. Its output goes to a file, so docker exec
    # doesn't wait for the master to end.
    if ! out=$(inside 'nginx > /tmp/nginx.out 2>&1 || { cat /tmp/nginx.out; exit 1; }'); then
        problem "nginx didn't start" "$out"
        return
    fi
    started=1
}

http_level_kept_out() {
    local path answer
    for path in / '/signin-discord?code=x&state=y'; do
        answer=$(from_cloudflare -H 'Accept-Encoding: gzip, br' -o /dev/null -D - "https://bot.murderminershub.com$path")
        expect "HTTP/1.1 200" "$answer" "the dashboard's $path"
        if grep -qi '^content-encoding' <<< "$answer"; then problem "the dashboard's $path left nginx compressed" "$answer"; fi
        refuse "X-From-Http-Level" "$answer" "the dashboard's $path"
    done
    answer=$(from_cloudflare -H 'Accept-Encoding: gzip' -o /dev/null -D - https://other.example/)
    expect "X-From-Http-Level: inherited" "$answer" "the account's site, which keeps the http level's header"
    expect "Content-Encoding: gzip" "$answer" "the account's site, which the http level's gzip compresses"
}

headers_replaced() {
    local answer
    answer=$(from_cloudflare -H 'X-Forwarded-For: 198.51.100.1' -H 'X-Forwarded-Proto: http' \
        -H 'X-MinerBot-Proxy-Key: visitor-made-this-up' -H 'X-Forwarded-Host: evil.example' -H 'X-Forwarded-Port: 8443' \
        -H 'X-Forwarded-Prefix: /x' -H 'Forwarded: for=192.0.2.66;host=evil.example' -H 'X-Real-IP: 192.0.2.66' \
        -H 'Cf-Access-Jwt-Assertion: token-from-access' -H 'User-Agent: AgentSecret/1.0' \
        'https://bot.murderminershub.com/signin-discord?code=SECRETCODE&state=SECRETSTATE')
    expect "HTTP/1.1 200" "$answer" "Cloudflare's request"
    expect "host=bot.murderminershub.com key=$key xff=$visitor proto=https fhost=[] fport=[] fprefix=[] fwd=[] realip=[] jwt=[token-from-access] cfip=[$visitor]" \
        "$answer" "the headers the dashboard gets"
}

others_refused() {
    local answer
    answer=$(request "$cloudflare" https://bot.murderminershub.com/)
    expect "HTTP/1.1 403" "$answer" "Cloudflare's address without CF-Connecting-IP, on 443"
    answer=$(request "$cloudflare" http://bot.murderminershub.com/)
    expect "HTTP/1.1 403" "$answer" "Cloudflare's address without CF-Connecting-IP, on 80"
    answer=$(request "$elsewhere" -H "CF-Connecting-IP: $visitor" https://bot.murderminershub.com/)
    expect "HTTP/1.1 403" "$answer" "outside Cloudflare, on 443"
    answer=$(request "$elsewhere" -H "CF-Connecting-IP: $visitor" http://bot.murderminershub.com/)
    expect "HTTP/1.1 403" "$answer" "outside Cloudflare, on 80"
}

port_80_redirects() {
    local answer
    answer=$(from_cloudflare 'http://bot.murderminershub.com/admin?x=1')
    expect "HTTP/1.1 301" "$answer" "port 80"
    expect "Location: https://bot.murderminershub.com/admin?x=1" "$answer" "port 80's redirect"
}

other_sites_answer() {
    local answer
    answer=$(from_cloudflare https://other.example/)
    expect "account site" "$answer" "the account's own site"
    answer=$(from_cloudflare https://unknown.example/)
    expect "default server" "$answer" "an unknown name"
}

big_header_passes() {
    local answer
    answer=$(from_cloudflare https://bot.murderminershub.com/big)
    expect "HTTP/1.1 200" "$answer" "a 12 KB response header"
}

bodies_capped() {
    local answer errors
    answer=$(post 204800)
    expect "HTTP/1.1 200" "$answer" "a 200 KB body"
    answer=$(post 307200)
    expect "HTTP/1.1 413" "$answer" "a 300 KB body"
    errors=$(inside 'cat /var/log/nginx/minerbot-dashboard.error.log 2> /dev/null' || true)
    refuse "buffered to a temporary file" "$errors" "the error log after the bodies"
}

access_log_plain() {
    local log secret
    from_cloudflare -A 'AgentSecret/1.0' -e 'https://referer.example/REFERERSECRET' -b 'session=COOKIESECRET' \
        'https://bot.murderminershub.com/signin-discord?code=SECRETCODE&state=SECRETSTATE' > /dev/null
    log=$(inside 'cat /var/log/nginx/minerbot-dashboard.access.log' 2>&1)
    expect "$visitor [" "$log" "the log's visitor address"
    expect '"GET /signin-discord HTTP/1.1" 200' "$log" "the log's request"
    for secret in SECRETCODE SECRETSTATE AgentSecret REFERERSECRET COOKIESECRET; do
        refuse "$secret" "$log" "the access log"
    done
}

dashboard_down() {
    local out answer errors log
    out=$(inside 'rm /etc/nginx/conf.d/fake-dashboard.conf' 2>&1) || problem "couldn't remove the stand-in dashboard" "$out"
    out=$(reload 2>&1) || problem "nginx didn't reload without the stand-in dashboard" "$out"
    answer=$(from_cloudflare https://bot.murderminershub.com/admin)
    expect "HTTP/1.1 503" "$answer" "the dashboard down"
    expect "Retry-After: 30" "$answer" "the dashboard down"
    expect "Cache-Control: no-store" "$answer" "the dashboard down"
    expect "Content-Type: text/plain; charset=utf-8" "$answer" "the dashboard down"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "the dashboard down"
    from_cloudflare 'https://bot.murderminershub.com/signin-discord?code=DOWNCODE&state=DOWNSTATE' > /dev/null
    from_cloudflare 'https://bot.murderminershub.com/SIGNIN-Discord?code=UPPERCODE&state=UPPERSTATE' > /dev/null
    from_cloudflare 'https://bot.murderminershub.com/admin?q=VISIBLEQUERY' > /dev/null
    errors=$(inside 'cat /var/log/nginx/minerbot-dashboard.error.log' 2>&1)
    expect "VISIBLEQUERY" "$errors" "the error log for another address"
    refuse "DOWNCODE" "$errors" "the error log"
    refuse "UPPERCODE" "$errors" "the error log"
    log=$(inside 'cat /var/log/nginx/minerbot-dashboard.access.log' 2>&1)
    expect '"GET /signin-discord HTTP/1.1" 503 ' "$log" "the access log while down"
}

key_file_optional() {
    local out
    out=$(inside 'mv /etc/nginx/minerbot/proxy-key.conf /etc/nginx/minerbot/proxy-key.off && nginx -t -q 2>&1') \
        || problem "nginx -t refused the files without the proxy key's file" "$out"
    out=$(inside 'mv /etc/nginx/minerbot/proxy-key.off /etc/nginx/minerbot/proxy-key.conf' 2>&1) \
        || problem "couldn't put the proxy key's file back" "$out"
}

cloudflare_list_optional() {
    local out answer
    # The account's block includes the list plainly, as cPanel's blocks do, so it goes too
    out=$(inside 'mv /etc/nginx/conf.d/includes-optional/cloudflare.conf /tmp/cloudflare.conf \
        && rm /etc/nginx/conf.d/users.conf && nginx -t -q 2>&1') \
        || problem "nginx -t refused the files without cPanel's Cloudflare list" "$out"
    out=$(reload 2>&1) || problem "nginx didn't reload without cPanel's Cloudflare list" "$out"
    answer=$(from_cloudflare https://bot.murderminershub.com/admin)
    expect "HTTP/1.1 403" "$answer" "Cloudflare's request without cPanel's list"
    out=$(inside 'mv /tmp/cloudflare.conf /etc/nginx/conf.d/includes-optional/cloudflare.conf' 2>&1) \
        || problem "couldn't put the Cloudflare list back" "$out"
}

# The candidate on its own, before it goes near conf.d: absolute paths, its files staged elsewhere, and pipelog left out.
# setup-dashboard.sh also puts nginx.conf's main-context lines first; nginx-procedure-test.sh checks that part.
candidate_alone() {
    local out
    out=$(inside 'mkdir -p /tmp/check/staged \
        && cp /etc/nginx/minerbot/origin-cert.pem /etc/nginx/minerbot/origin-key.pem /etc/nginx/minerbot/proxy-key.conf /tmp/check/staged/ \
        && sed -e "s#/etc/nginx/minerbot/#/tmp/check/staged/#g" -e "/pipelog/d" /etc/nginx/conf.d/minerbot-dashboard.conf > /tmp/check/candidate.conf \
        && printf "events {}\nhttp {\n    include /tmp/check/candidate.conf;\n}\n" > /tmp/check/nginx.conf \
        && nginx -t -q -c /tmp/check/nginx.conf 2>&1') \
        || problem "nginx -t -c refused the candidate on its own" "$out"
}

missing_key_caught() {
    local out
    out=$(inside 'rm /tmp/check/staged/origin-key.pem' 2>&1) || problem "couldn't remove the staged key" "$out"
    if out=$(inside 'nginx -t -q -c /tmp/check/nginx.conf 2>&1'); then
        problem "nginx -t -c passed without the key"
    else
        expect "origin-key.pem" "$out" "nginx's message without the key"
    fi
}

relative_path_from_prefix() {
    local out
    out=$(inside 'cd /tmp && nginx -t -q -c check/nginx.conf 2>&1' || true)
    expect "/etc/nginx/check/nginx.conf" "$out" "nginx -t -c check/nginx.conf, run in /tmp"
}

# ---------- the files nginx gets

if [ -n "${MINERBOT_TEST_NGINX_CONF:-}" ]; then
    echo "Checking $conf (named by MINERBOT_TEST_NGINX_CONF, not the repository's file) in $image"
else
    echo "Checking $conf in $image"
fi
[ -f "$conf" ] || stop "There's no file at $conf."
timeout -k 10 30 docker version > /dev/null 2>&1 || stop "Docker isn't running, or doesn't answer."
if ! timeout -k 10 30 docker image inspect "$image" > /dev/null 2>&1; then
    timeout -k 10 300 docker pull -q "$image" > /dev/null || stop "Couldn't pull $image within 5 minutes."
fi

# A scratch folder in TMPDIR, or /tmp, removed at the end
work=$(mktemp -d "${TMPDIR:-/tmp}/mbtest-nginx.XXXXXX")
mkdir -p "$work/conf.d/includes-optional" "$work/conf.d/users" "$work/minerbot"
grep -vxE "$pipelog_line" "$conf" > "$work/conf.d/minerbot-dashboard.conf" || [ $? = 1 ] || stop "Couldn't read $conf."
# Relative paths from here on, which docker and openssl read the same way in Git Bash and in Linux
cd "$work"

# The shape of cPanel's list of Cloudflare's addresses, with one address instead of Cloudflare's 22 ranges
printf 'set_real_ip_from %s;\nreal_ip_header CF-Connecting-IP;\n' "$cloudflare_ip" > conf.d/includes-optional/cloudflare.conf
printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$key" > minerbot/proxy-key.conf
if ! out=$(timeout -k 10 60 openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=bot.murderminershub.com" \
    -addext "subjectAltName=DNS:bot.murderminershub.com" -keyout minerbot/origin-key.pem -out minerbot/origin-cert.pem 2>&1); then
    stop "openssl couldn't make the test certificate: $out"
fi

# Like cPanel's default.conf, and like an account's own block with cPanel's exact listen lines
cat > conf.d/default.conf << 'EOF'
server {
    listen 80 default_server reuseport;
    listen [::]:80 default_server reuseport;
    listen 443 ssl default_server reuseport;
    listen [::]:443 ssl default_server reuseport;
    ssl_certificate /etc/nginx/minerbot/origin-cert.pem;
    ssl_certificate_key /etc/nginx/minerbot/origin-key.pem;
    return 404 "default server\n";
}
EOF
cat > conf.d/users/acct.conf << 'EOF'
server {
    listen 80;
    listen [::]:80;
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 off;
    server_name other.example;
    ssl_certificate /etc/nginx/minerbot/origin-cert.pem;
    ssl_certificate_key /etc/nginx/minerbot/origin-key.pem;
    include conf.d/includes-optional/cloudflare.conf;
    default_type text/plain;
    return 200 "account site, long enough for the http level's gzip to compress\n";
}
EOF
printf 'include /etc/nginx/conf.d/users/*.conf;\n' > conf.d/users.conf
# What cPanel's http level could hold: compression on, and a header for every site. The body limit is the one cPanel's
# ea-nginx.conf sets.
cat > conf.d/00-http-level.conf << 'EOF'
gzip on;
gzip_types text/plain;
add_header X-From-Http-Level "inherited";
client_max_body_size 128m;
EOF
# The dashboard, played by nginx: it answers with what it was sent, and /big with a 12 KB header
printf -v big '%3000s' ''
big=${big// /b}
cat > conf.d/fake-dashboard.conf << EOF
server {
    listen 127.0.0.1:5080;
    # Kestrel never compresses, and never sends the http level's header
    gzip off;
    add_header_inherit off;
    location / {
        default_type text/plain;
        return 200 "host=\$http_host key=\$http_x_minerbot_proxy_key xff=\$http_x_forwarded_for proto=\$http_x_forwarded_proto fhost=[\$http_x_forwarded_host] fport=[\$http_x_forwarded_port] fprefix=[\$http_x_forwarded_prefix] fwd=[\$http_forwarded] realip=[\$http_x_real_ip] jwt=[\$http_cf_access_jwt_assertion] cfip=[\$http_cf_connecting_ip] uri=\$request_uri\n";
    }
    location = /big {
        add_header X-Big1 "$big";
        add_header X-Big2 "$big";
        add_header X-Big3 "$big";
        add_header X-Big4 "$big";
        return 200 "big\n";
    }
}
EOF

# ---------- the containers

out=$(timeout -k 10 60 docker network create --subnet "$subnet" "$net" 2>&1) \
    || stop "Couldn't make the network $net: $out. A network left by an earlier run may hold $subnet; see docker network ls --filter name=mbtest-nginx-"
# nginx listens on [::] as well, as the server's blocks do
run_container "$nginx" "$nginx_ip" --sysctl net.ipv6.conf.all.disable_ipv6=0
run_container "$cloudflare" "$cloudflare_ip"
run_container "$elsewhere" "$elsewhere_ip"
out=$(timeout -k 10 60 docker cp conf.d/. "$nginx:/etc/nginx/conf.d/" 2>&1) || stop "Couldn't copy conf.d into nginx's container: $out"
out=$(timeout -k 10 60 docker cp minerbot "$nginx:/etc/nginx/minerbot" 2>&1) || stop "Couldn't copy minerbot/ into nginx's container: $out"

# ---------- the checks
# Each check's function notes its own problems, and runs with || true, so one failed check never ends the run

pipelog_twice || true
verdict "the file has the line \"pipelog off;\" exactly twice, one for each server (the tested copy leaves both out)"
nginx_starts || true
verdict "nginx -t passes, and nginx starts, next to a reuseport default server and an account block with cPanel's listen lines"
[ -n "$started" ] || stop "nginx isn't running, so the other $((checks - made)) checks can't run."
http_level_kept_out || true
verdict "with the http level's gzip on and add_header, the dashboard's responses are neither compressed nor given that header, while the account's site gets both"
headers_replaced || true
verdict "from Cloudflare, forged headers become the fixed Host, nginx's key, the visitor's address and https; X-Forwarded-Host, -Port and -Prefix, Forwarded and X-Real-IP are emptied; the Access token passes"
others_refused || true
verdict "403 from outside Cloudflare, and from Cloudflare without CF-Connecting-IP, on 443 and on 80"
port_80_redirects || true
verdict "port 80 sends Cloudflare's visitors to https with a 301 that keeps the query"
other_sites_answer || true
verdict "the account's site and the default server still answer their own names"
big_header_passes || true
verdict "a 12 KB response header passes (the 16k proxy buffer)"
bodies_capped || true
verdict "a 200 KB body passes in memory, with no temporary-file warning, and a 300 KB body gets 413"
access_log_plain || true
verdict "the access log has the visitor's address and the path, without the query, user agent, referer or cookies"
dashboard_down || true
verdict "with the dashboard down: a plain 503 with Retry-After; Discord's callback, in any letter case, leaves nothing in the error log, which still logs other addresses"
key_file_optional || true
verdict "without the proxy key's file, nginx -t still passes"
cloudflare_list_optional || true
verdict "without cPanel's Cloudflare list, nginx still loads, and refuses everyone here"
candidate_alone || true
verdict "the candidate passes nginx -t -c on its own, with its files staged elsewhere"
missing_key_caught || true
verdict "on its own, a missing certificate key fails the test"
relative_path_from_prefix || true
verdict "a relative -c path is read against nginx's prefix, not the current folder, so the path must be absolute"

echo "$passed of $checks passed"
if [ "$made" != "$checks" ]; then
    echo "This script made $made checks, but checks= at its top says $checks. Correct that count."
    exit 1
fi
[ "$passed" = "$checks" ] || exit 1

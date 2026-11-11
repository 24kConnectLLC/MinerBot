#!/usr/bin/env bash
# Checks deploy/minerbot-dashboard.conf in nginx's own image, at the server's nginx version, before it can reach the
# server, where a broken file would stop nginx from reloading for every site. nginx gets the file next to a default
# server, an account block like cPanel's and the parent domain's own block, an http level that turns gzip on, adds a
# header and keeps an access log, as cPanel's could, and a stand-in dashboard on the dashboard's Unix socket,
# /run/minerbot-dashboard/dashboard.sock, in a folder laid out as on the server. Two clients send the requests: one from
# an address in the stand-in Cloudflare list, and one from elsewhere.
#
# The file must include /etc/nginx/minerbot/pipelog.con[f] once in each of its two blocks and never name pipelog itself,
# which nginx refuses for every site when cPanel's pipelog module isn't loaded. nginx gets the file as it is. Its image
# has no pipelog module, so pipelog.conf is empty here, as setup writes it on a server with WHM's piped logging off; one
# check fills it to prove nginx reads it at that path.
#
# Run it from Git Bash or Linux: bash deploy/tests/nginx-test.sh. It needs Docker and openssl. It prints one line per
# check, then "N of M passed", and exits 1 if any check failed. To try a deliberate break, point
# MINERBOT_TEST_NGINX_CONF at a changed copy of the file, and the run tests that copy instead.
#
# Everything it makes in Docker is named mbtest-nginx-<its PID>-... and removed when it ends. Docker picks a free subnet
# for each run, so runs side by side don't clash. Each container's main process is sleep 600, so even a run killed
# before it can clean up leaves nothing running after 10 minutes. A killed run does leave its network, and its scratch
# folder in TMPDIR: docker network prune --filter label=mbtest-nginx removes such networks, and only the ones no
# container uses.
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
# Read from Docker once the containers run, since Docker picks the network's subnet
nginx_ip=""
cloudflare_ip=""
# The visitor's address, as Cloudflare's CF-Connecting-IP header gives it
visitor=203.0.113.7
key=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef   # made up
# Where the dashboard listens on the server, and where the stand-in listens here
socket=/run/minerbot-dashboard/dashboard.sock
# A page far larger than nginx's proxy buffers and the largest default socket buffers: 32 MiB
large_page=33554432
# The line that includes pipelog.conf, give or take spaces, and any line that names pipelog itself
include_line='[[:space:]]*include /etc/nginx/minerbot/pipelog\.con\[f\];[[:space:]]*'
pipelog_line='^[[:space:]]*pipelog[[:space:]]'
checks=26   # how many checks this script makes
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
has_line() {   # has_line <line> <answer> <what>: the answer must have exactly that line, as a header has it
    grep -qxF -- "$1" <<< "${2//$'\r'/}" || problem "$3: expected the whole line '$1'" "$2"
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

run_container() {   # run_container <name> [<docker run options...>]: one that ends by itself after 10 minutes
    local name=$1 out
    shift
    out=$(timeout -k 10 120 docker run -d --rm --name "$name" --network "$net" "$@" "$image" sleep 600 2>&1) \
        || stop "Couldn't start the container $name: $out"
}
address_of() {   # address_of <container>: its address on this run's network, or why there's none
    local out
    if ! out=$(timeout -k 10 30 docker inspect -f "{{(index .NetworkSettings.Networks \"$net\").IPAddress}}" "$1" 2>&1); then
        printf '%s\n' "$out"
        return 1
    fi
    if ! [[ $out =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        printf "Docker gave it '%s', not an IPv4 address\n" "$out"
        return 1
    fi
    printf '%s\n' "$out"
}
inside() {   # inside <shell command>: runs it in nginx's container
    timeout -k 10 30 docker exec "$nginx" sh -c "$1"
}
request() {   # request <client> <curl arguments...>: nginx's answer, headers first, or curl's error
    local client=$1 name
    local -a names=()
    shift
    for name in bot.murderminershub.com murderminershub.com www2.murderminershub.com; do
        names+=(--resolve "$name:443:$nginx_ip" --resolve "$name:80:$nginx_ip")
    done
    timeout -k 10 30 docker exec "$client" curl -sSk -i --max-time 10 "${names[@]}" \
        --resolve "other.example:443:$nginx_ip" --resolve "unknown.example:443:$nginx_ip" "$@" 2>&1 || true
}
from_cloudflare() {   # from_cloudflare <curl arguments...>: a visitor's request, as Cloudflare passes it on
    request "$cloudflare" -H "CF-Connecting-IP: $visitor" "$@"
}
post() {   # post <bytes> [chunked]: a body of that size from a visitor, through Cloudflare, with Content-Length or chunked
    local framing="X-Body: content-length"
    if [ "${2:-}" = chunked ]; then framing="Transfer-Encoding: chunked"; fi
    timeout -k 10 30 docker exec "$cloudflare" sh -c "head -c $1 /dev/zero | curl -sSk -i --max-time 10 \
        --resolve bot.murderminershub.com:443:$nginx_ip -H 'CF-Connecting-IP: $visitor' -H '$framing' --data-binary @- \
        https://bot.murderminershub.com/form" 2>&1 || true
}
error_log() {   # the dashboard's error log, as nginx has written it so far
    inside 'cat /var/log/nginx/minerbot-dashboard.error.log 2> /dev/null' || true
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
# The tested copy with one directive changed, for a check that needs it, and back. Each one's verdict says why.
change_tested_copy() {   # change_tested_copy <sed expression> <the changed line>
    local out
    out=$(inside "cp /etc/nginx/conf.d/minerbot-dashboard.conf /tmp/minerbot-dashboard.conf.keep \
        && sed -i '$1' /etc/nginx/conf.d/minerbot-dashboard.conf \
        && grep -qF '$2' /etc/nginx/conf.d/minerbot-dashboard.conf" 2>&1) \
        || problem "couldn't change the tested copy to have '$2'" "$out"
    out=$(reload 2>&1) || problem "nginx didn't reload with '$2'" "$out"
}
put_tested_copy_back() {
    local out
    out=$(inside 'mv /tmp/minerbot-dashboard.conf.keep /etc/nginx/conf.d/minerbot-dashboard.conf' 2>&1) \
        || problem "couldn't put the tested copy back" "$out"
    out=$(reload 2>&1) || problem "nginx didn't reload with the tested copy put back" "$out"
}

# ---------- the checks, in the order they run

pipelog_included() {
    local count named
    count=$(grep -cxE "$include_line" "$conf" || true)
    if [ "$count" != 2 ]; then problem "the include line is there $count time(s), not 2"; fi
    named=$(grep -nE "$pipelog_line" "$conf" || true)
    [ -z "$named" ] || problem "the file names pipelog itself" "$named"
}

# A pipelog line in pipelog.conf, which this nginx can't load, fails nginx -t there: so nginx reads the file at the path
# the include names. The [f] pattern would otherwise hide a wrong path.
pipelog_conf_read() {
    local out
    if out=$(inside 'printf "pipelog off;\n" > /etc/nginx/minerbot/pipelog.conf && nginx -t -q 2>&1'); then
        problem "nginx -t passed with a pipelog line in pipelog.conf, so nginx doesn't read it"
    else
        expect 'unknown directive "pipelog" in /etc/nginx/minerbot/pipelog.conf:1' "$out" "nginx -t's message"
    fi
    out=$(inside ': > /etc/nginx/minerbot/pipelog.conf && nginx -t -q 2>&1') \
        || problem "nginx -t fails with pipelog.conf empty again" "$out"
}

nginx_starts() {
    local out
    if ! out=$(inside 'nginx -t -q 2>&1'); then
        problem "nginx -t refused the files" "$out"
        return
    fi
    # nginx only warns, and passes, when our file names a server another block names too, such as a wider server_name
    # that takes the parent domain's own name from its account's block, which then loses it
    [ -z "$out" ] || problem "nginx -t passed, but warned" "$out"
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
    local answer path
    # Both locations that pass requests on: location /, which serves every page, and Discord's callback. A
    # proxy_set_header in either one would drop every server-level line there, so each is checked on its own.
    for path in /admin '/signin-discord?code=SECRETCODE&state=SECRETSTATE'; do
        answer=$(from_cloudflare -H 'X-Forwarded-For: 198.51.100.1' -H 'X-Forwarded-Proto: http' \
            -H 'X-MinerBot-Proxy-Key: visitor-made-this-up' -H 'X-Forwarded-Host: evil.example' -H 'X-Forwarded-Port: 8443' \
            -H 'X-Forwarded-Prefix: /x' -H 'Forwarded: for=192.0.2.66;host=evil.example' -H 'X-Real-IP: 192.0.2.66' \
            -H 'Cf-Access-Jwt-Assertion: token-from-access' -H 'User-Agent: AgentSecret/1.0' \
            "https://bot.murderminershub.com$path")
        expect "HTTP/1.1 200" "$answer" "Cloudflare's request to ${path%%\?*}"
        expect "host=bot.murderminershub.com key=$key xff=$visitor proto=https fhost=[] fport=[] fprefix=[] fwd=[] realip=[] jwt=[token-from-access] cfip=[$visitor]" \
            "$answer" "the headers the dashboard gets for ${path%%\?*}"
    done
}

# nginx picks this block by the name alone, so a Host with a port or in other letters still reaches it, and so does an
# absolute address in the request line, whatever Host header comes beside it. The dashboard builds Discord's redirect
# address from the Host it gets, and answers its health checks only to Host localhost, so it must always get the
# fixed name.
host_fixed() {
    local answer sent
    for sent in 'bot.murderminershub.com:443' 'BOT.MurderMinersHub.COM'; do
        answer=$(from_cloudflare -H "Host: $sent" https://bot.murderminershub.com/)
        expect "HTTP/1.1 200" "$answer" "Cloudflare's request with Host $sent"
        expect "host=bot.murderminershub.com key=" "$answer" "the Host the dashboard gets for Host $sent"
    done
    answer=$(from_cloudflare --request-target https://bot.murderminershub.com/health/live -H 'Host: localhost' \
        https://bot.murderminershub.com/)
    expect "HTTP/1.1 200" "$answer" "an absolute address in the request line, beside Host localhost"
    expect "host=bot.murderminershub.com key=" "$answer" "the Host the dashboard gets beside an absolute address"
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
    local answer host
    answer=$(from_cloudflare 'http://bot.murderminershub.com/admin?x=1')
    expect "HTTP/1.1 301" "$answer" "port 80"
    has_line "Location: https://bot.murderminershub.com/admin?x=1" "$answer" "port 80's redirect"
    # The redirect names the dashboard itself, never what the request said: no port, letter case or final dot from
    # Host, and not Host's name when the request line carries the whole address
    for host in bot.murderminershub.com:80 BOT.MurderMinersHub.com bot.murderminershub.com.; do
        answer=$(from_cloudflare -H "Host: $host" 'http://bot.murderminershub.com/admin?x=1')
        expect "HTTP/1.1 301" "$answer" "port 80 with Host: $host"
        has_line "Location: https://bot.murderminershub.com/admin?x=1" "$answer" "port 80's redirect with Host: $host"
    done
    answer=$(from_cloudflare --request-target 'http://bot.murderminershub.com/admin?x=1' -H 'Host: elsewhere.example' \
        http://bot.murderminershub.com/)
    has_line "Location: https://bot.murderminershub.com/admin?x=1" "$answer" "port 80's redirect with another name in Host"
}

other_sites_answer() {
    local answer
    answer=$(from_cloudflare https://other.example/)
    expect "account site" "$answer" "the account's own site"
    answer=$(from_cloudflare https://unknown.example/)
    expect "default server" "$answer" "an unknown name"
}

# A server_name wider than the dashboard's own name, such as .murderminershub.com, would take the parent domain's own
# name from its account's block, which nginx reads after ours, and every other name under it from the default server
parent_domain_kept() {
    local answer scheme
    for scheme in https http; do
        answer=$(from_cloudflare "$scheme://murderminershub.com/")
        expect "parent site" "$answer" "the parent domain's own name on $scheme, through Cloudflare"
        answer=$(from_cloudflare "$scheme://www2.murderminershub.com/")
        expect "default server" "$answer" "another name under the parent domain on $scheme, through Cloudflare"
        answer=$(request "$elsewhere" "$scheme://www2.murderminershub.com/")
        expect "default server" "$answer" "another name under the parent domain on $scheme, from elsewhere"
    done
}

big_header_passes() {
    local answer
    answer=$(from_cloudflare https://bot.murderminershub.com/big)
    expect "HTTP/1.1 200" "$answer" "a 12 KB response header"
}

# A sign-in waits on up to three Discord calls of 10 seconds each and then the database, and a page can wait on Access's
# keys or the database, so neither location may give up early. The stand-in answers these after 8 seconds; the three
# wait side by side.
slow_answer_passes() {
    local path i=0 answer
    local -a paths=('/admin?mbtest_slow=8' '/signin-discord?mbtest_slow=8&code=SLOWCODE&state=SLOWSTATE' \
        '/SIGNIN-Discord?mbtest_slow=8')
    for path in "${paths[@]}"; do
        from_cloudflare --max-time 25 "https://bot.murderminershub.com$path" > "slow-$i.out" &
        i=$((i + 1))
    done
    wait
    i=0
    for path in "${paths[@]}"; do
        answer=$(cat "slow-$i.out")
        expect "HTTP/1.1 200" "$answer" "${path%%\?*}, answered after 8 seconds"
        expect "slow answer" "$answer" "${path%%\?*}, answered after 8 seconds"
        i=$((i + 1))
    done
}

bodies_capped() {
    local answer errors framing
    # The cap is exactly 256 KB, 262144 bytes, and a body nginx takes stays in memory, sent either way
    for framing in content-length chunked; do
        answer=$(post 262144 "$framing")
        expect "HTTP/1.1 200" "$answer" "a body of exactly 262144 bytes ($framing)"
        answer=$(post 262145 "$framing")
        expect "HTTP/1.1 413" "$answer" "a body of 262145 bytes ($framing)"
    done
    errors=$(error_log)
    refuse "a client request body is buffered to a temporary file" "$errors" "the error log after the bodies"
}

# The page arrives as the visitor reads it: nginx waits for a visitor who pauses instead of writing the rest to disk
large_page_in_memory() {
    local out errors
    out=$(inside "dd if=/dev/zero of=/tmp/large-page bs=1 count=0 seek=$large_page 2>&1 && chmod 644 /tmp/large-page") \
        || problem "couldn't make the large page" "$out"
    out=$(timeout -k 10 60 docker exec "$cloudflare" sh -c "curl -sSk --max-time 40 \
        --resolve bot.murderminershub.com:443:$nginx_ip -H 'CF-Connecting-IP: $visitor' \
        https://bot.murderminershub.com/large | { sleep 3; wc -c; }" 2>&1 || true)
    expect "$large_page" "$out" "the large page's size at the visitor"
    errors=$(error_log)
    refuse "an upstream response is buffered to a temporary file" "$errors" "the error log after the large page"
    inside 'rm -f /tmp/large-page' > /dev/null 2>&1 || true
}

# The two refusals of temporary files above can fail only if warnings reach the dashboard's error log. With a 16 KB body
# buffer, the 200 KB body's warning must be there.
temp_file_warning_logged() {
    local answer
    change_tested_copy 's/client_body_buffer_size 256k;/client_body_buffer_size 16k;/' 'client_body_buffer_size 16k;'
    answer=$(post 204800)
    expect "HTTP/1.1 200" "$answer" "a 200 KB body with a 16 KB buffer"
    expect "a client request body is buffered to a temporary file" "$(error_log)" "the error log with a 16 KB buffer"
    put_tested_copy_back
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

# Port 80's server keeps its own access log too: without one, it would fall back to the http level's, which logs whole
# request lines, queries included
port_80_logged_plain() {
    local log others secret
    from_cloudflare -A 'AgentPort80/1.0' -e 'https://referer.example/REFERERPORT80' -b 'session=COOKIEPORT80' \
        'http://bot.murderminershub.com/port80?code=CODEPORT80&state=STATEPORT80' > /dev/null
    log=$(inside 'cat /var/log/nginx/minerbot-dashboard.access.log' 2>&1)
    expect '"GET /port80 HTTP/1.1" 301' "$log" "the dashboard's log, for port 80"
    for secret in CODEPORT80 STATEPORT80 AgentPort80 REFERERPORT80 COOKIEPORT80; do
        refuse "$secret" "$log" "the dashboard's log"
    done
    others=$(inside 'cat /var/log/nginx/http-level.access.log' 2>&1)
    # The account's site shows that this log is written at all
    expect '"GET / HTTP/1.1" 200' "$others" "the http level's log, for the account's site"
    for secret in port80 CODEPORT80 STATEPORT80 AgentPort80 REFERERPORT80 signin-discord SECRETCODE; do
        refuse "$secret" "$others" "the http level's log"
    done
}

# A dashboard that takes the connection but answers later than proxy_read_timeout gets nginx's 504, which must become
# the same plain 503 as a stopped one. The tested copy waits 2 seconds instead of 60 here, and the stand-in 4.
dashboard_too_slow() {
    local answer errors
    change_tested_copy 's/^\([[:space:]]*\)proxy_read_timeout 60s;/\1proxy_read_timeout 2s;/' 'proxy_read_timeout 2s;'
    answer=$(from_cloudflare 'https://bot.murderminershub.com/admin?mbtest_slow=4&q=SLOWQUERY')
    expect "HTTP/1.1 503" "$answer" "a page slower than proxy_read_timeout"
    has_line "Retry-After: 30" "$answer" "a page slower than proxy_read_timeout"
    has_line "Cache-Control: no-store" "$answer" "a page slower than proxy_read_timeout"
    has_line "Content-Type: text/plain; charset=utf-8" "$answer" "a page slower than proxy_read_timeout"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "a page slower than proxy_read_timeout"
    answer=$(from_cloudflare 'https://bot.murderminershub.com/signin-discord?mbtest_slow=4&code=SLOWCODE&state=SLOWSTATE')
    expect "HTTP/1.1 503" "$answer" "Discord's callback, slower than proxy_read_timeout"
    errors=$(error_log)
    expect "SLOWQUERY" "$errors" "the error log, for a page that timed out"
    refuse "SLOWCODE" "$errors" "the error log, for Discord's callback that timed out"
    # The stand-in's own answers end 2 seconds later, which the reload below waits for
    put_tested_copy_back
}

dashboard_down() {
    local out answer errors log
    # A second name for the stand-in's socket, which outlives it, for the next check
    out=$(inside "ln $socket /run/minerbot-dashboard/left-behind.sock" 2>&1) \
        || problem "couldn't give the stand-in's socket a second name" "$out"
    out=$(inside 'rm /etc/nginx/conf.d/fake-dashboard.conf' 2>&1) || problem "couldn't remove the stand-in dashboard" "$out"
    out=$(reload 2>&1) || problem "nginx didn't reload without the stand-in dashboard" "$out"
    inside "[ ! -e $socket ]" || problem "the stand-in's socket is still there, so this check can't show a missing one"
    answer=$(from_cloudflare https://bot.murderminershub.com/admin)
    expect "HTTP/1.1 503" "$answer" "the dashboard down"
    has_line "Retry-After: 30" "$answer" "the dashboard down"
    has_line "Cache-Control: no-store" "$answer" "the dashboard down"
    has_line "Content-Type: text/plain; charset=utf-8" "$answer" "the dashboard down"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "the dashboard down"
    from_cloudflare 'https://bot.murderminershub.com/signin-discord?code=DOWNCODE&state=DOWNSTATE' > /dev/null
    from_cloudflare 'https://bot.murderminershub.com/SIGNIN-Discord?code=UPPERCODE&state=UPPERSTATE' > /dev/null
    from_cloudflare 'https://bot.murderminershub.com/admin?q=VISIBLEQUERY' > /dev/null
    # Addresses that only look like the callback stay in the error log: the quiet location is the callback alone
    from_cloudflare 'https://bot.murderminershub.com/signin-discordx?q=LONGERQUERY' > /dev/null
    from_cloudflare 'https://bot.murderminershub.com/signin-discord/x?q=DEEPERQUERY' > /dev/null
    from_cloudflare 'https://bot.murderminershub.com/x/signin-discord?q=EARLIERQUERY' > /dev/null
    errors=$(error_log)
    expect "connect() to unix:$socket failed (2: No such file or directory)" "$errors" "the error log, for a missing socket"
    expect "VISIBLEQUERY" "$errors" "the error log for another address"
    expect "LONGERQUERY" "$errors" "the error log for /signin-discordx, which isn't the callback"
    expect "DEEPERQUERY" "$errors" "the error log for /signin-discord/x, which isn't the callback"
    expect "EARLIERQUERY" "$errors" "the error log for /x/signin-discord, which isn't the callback"
    refuse "DOWNCODE" "$errors" "the error log"
    refuse "UPPERCODE" "$errors" "the error log"
    log=$(inside 'cat /var/log/nginx/minerbot-dashboard.access.log' 2>&1)
    expect '"GET /signin-discord HTTP/1.1" 503 ' "$log" "the access log while down"
}

# What a dashboard killed outright leaves: its socket file, with nothing listening on it
socket_left_behind() {
    local out answer errors
    out=$(inside "mv /run/minerbot-dashboard/left-behind.sock $socket" 2>&1) \
        || problem "couldn't put the left-behind socket in the dashboard's place" "$out"
    answer=$(from_cloudflare 'https://bot.murderminershub.com/admin?q=REFUSEDQUERY')
    expect "HTTP/1.1 503" "$answer" "a socket nothing listens on"
    has_line "Retry-After: 30" "$answer" "a socket nothing listens on"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "a socket nothing listens on"
    expect "connect() to unix:$socket failed (111: Connection refused)" "$(error_log)" \
        "the error log, for a socket nothing listens on"
    from_cloudflare 'https://bot.murderminershub.com/signin-discord?code=REFUSEDCODE&state=REFUSEDSTATE' > /dev/null
    refuse "REFUSEDCODE" "$(error_log)" "the error log, for Discord's callback to a socket nothing listens on"
    # A socket nginx's workers can't open, as a wrong mode or group leaves it: nginx logs that as critical, with the
    # request line, and Discord's callback still leaves nothing
    out=$(inside "chmod 600 $socket" 2>&1) || problem "couldn't close the socket to nginx's workers" "$out"
    answer=$(from_cloudflare 'https://bot.murderminershub.com/admin?q=DENIEDQUERY')
    expect "HTTP/1.1 503" "$answer" "a socket nginx's workers can't open"
    from_cloudflare 'https://bot.murderminershub.com/signin-discord?code=DENIEDCODE&state=DENIEDSTATE' > /dev/null
    errors=$(error_log)
    expect "connect() to unix:$socket failed (13: Permission denied)" "$errors" \
        "the error log, for a socket nginx's workers can't open"
    expect "DENIEDQUERY" "$errors" "the error log for another address, with a socket nginx's workers can't open"
    refuse "DENIEDCODE" "$errors" "the error log, for Discord's callback with a socket nginx's workers can't open"
    out=$(inside "rm -f $socket" 2>&1) || problem "couldn't remove the left-behind socket" "$out"
}

# The dashboard used to listen on 127.0.0.1:5080, which any account on the server can take while the dashboard is down.
# nginx must never send anything there: here an unprivileged process listens on it, and must get nothing.
port_5080_left_alone() {
    local out answer got
    # The sleep keeps the listener's input open, so it reads and writes down whatever reaches it
    out=$(timeout -k 10 30 docker exec -d -u 4001 "$nginx" \
        sh -c 'sleep 30 | nc -l -s 127.0.0.1 -p 5080 > /tmp/port-5080.got 2>&1' 2>&1) \
        || problem "couldn't start the listener on 127.0.0.1:5080" "$out"
    # shellcheck disable=SC2016 # the $ are for the container's shell
    inside 'for i in $(seq 50); do grep -q "0100007F:13D8 00000000:0000 0A" /proc/net/tcp && exit 0; sleep 0.1; done; exit 1' \
        || problem "nothing listens on 127.0.0.1:5080, so this check proves nothing"
    answer=$(from_cloudflare -b 'session=SQUATCOOKIE' -H 'Cf-Access-Jwt-Assertion: SQUATTOKEN' \
        https://bot.murderminershub.com/admin)
    expect "HTTP/1.1 503" "$answer" "the dashboard down, with another process on 127.0.0.1:5080"
    expect "The MinerBot dashboard is restarting or stopped" "$answer" "the dashboard down, with another process on 127.0.0.1:5080"
    got=$(inside 'cat /tmp/port-5080.got 2> /dev/null' || true)
    [ -z "$got" ] || problem "the process on 127.0.0.1:5080 got a request" "$got"
    refuse "$key" "$got" "what the process on 127.0.0.1:5080 got"
    inside 'pkill -f "nc -l -s 127.0.0.1 -p 5080"' > /dev/null 2>&1 || true
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
    # The account's and the parent domain's blocks include the list plainly, as cPanel's blocks do, so they go too
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
mkdir -p "$work/conf.d/includes-optional" "$work/conf.d/users" "$work/minerbot" "$work/njs"
cp "$conf" "$work/conf.d/minerbot-dashboard.conf" || stop "Couldn't read $conf."
# Relative paths from here on, which docker and openssl read the same way in Git Bash and in Linux
cd "$work"

printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$key" > minerbot/proxy-key.conf
# As setup writes it where cPanel doesn't load its pipelog module
: > minerbot/pipelog.conf
if ! out=$(timeout -k 10 60 openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=bot.murderminershub.com" \
    -addext "subjectAltName=DNS:bot.murderminershub.com" -keyout minerbot/origin-key.pem -out minerbot/origin-cert.pem 2>&1); then
    stop "openssl couldn't make the test certificate: $out"
fi

# Like cPanel's default.conf, an account's own block with cPanel's exact listen lines, and the parent domain's block,
# which cPanel writes the same way. users.conf comes after our file, as on the server.
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
cat > conf.d/users/parent.conf << 'EOF'
server {
    listen 80;
    listen [::]:80;
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 off;
    server_name murderminershub.com www.murderminershub.com;
    ssl_certificate /etc/nginx/minerbot/origin-cert.pem;
    ssl_certificate_key /etc/nginx/minerbot/origin-key.pem;
    include conf.d/includes-optional/cloudflare.conf;
    default_type text/plain;
    return 200 "parent site\n";
}
EOF
printf 'include /etc/nginx/conf.d/users/*.conf;\n' > conf.d/users.conf
# What cPanel's http level could hold: compression on, a header for every site, and an access log with nginx's main
# format, which logs whole request lines. The body limit is the one cPanel's ea-nginx.conf sets.
cat > conf.d/00-http-level.conf << 'EOF'
gzip on;
gzip_types text/plain;
add_header X-From-Http-Level "inherited";
client_max_body_size 128m;
access_log /var/log/nginx/http-level.access.log main;
EOF
# The stand-in's slow answer, after as many seconds as the query's mbtest_slow says
cat > njs/slow.js << 'EOF'
function answer(r) {
    setTimeout(function () {
        r.headersOut['Content-Type'] = 'text/plain';
        r.return(200, 'slow answer\n');
    }, Number(r.args.mbtest_slow) * 1000);
}
export default { answer };
EOF
# The dashboard, played by nginx on the dashboard's socket: it answers with what it was sent, /big with a 12 KB header,
# /large with a 32 MiB page, and any address with mbtest_slow in its query only after that many seconds
printf -v big '%3000s' ''
big=${big// /b}
cat > conf.d/fake-dashboard.conf << EOF
server {
    listen unix:$socket;
    # Kestrel never compresses, never sends the http level's header, and never writes nginx's logs
    gzip off;
    add_header_inherit off;
    access_log off;
    js_import slow from /etc/nginx/njs/slow.js;
    location / {
        if (\$arg_mbtest_slow) {
            rewrite ^ /mbtest-slow last;
        }
        default_type text/plain;
        return 200 "host=\$http_host key=\$http_x_minerbot_proxy_key xff=\$http_x_forwarded_for proto=\$http_x_forwarded_proto fhost=[\$http_x_forwarded_host] fport=[\$http_x_forwarded_port] fprefix=[\$http_x_forwarded_prefix] fwd=[\$http_forwarded] realip=[\$http_x_real_ip] jwt=[\$http_cf_access_jwt_assertion] cfip=[\$http_cf_connecting_ip] uri=\$request_uri\n";
    }
    location = /mbtest-slow {
        internal;
        js_content slow.answer;
    }
    location = /big {
        add_header X-Big1 "$big";
        add_header X-Big2 "$big";
        add_header X-Big3 "$big";
        add_header X-Big4 "$big";
        return 200 "big\n";
    }
    location = /large {
        default_type application/octet-stream;
        alias /tmp/large-page;
    }
}
EOF

# ---------- the containers

out=$(timeout -k 10 60 docker network create --label mbtest-nginx "$net" 2>&1) || stop "Couldn't make the network $net: $out"
# nginx listens on [::] as well, as the server's blocks do
run_container "$nginx" --sysctl net.ipv6.conf.all.disable_ipv6=0
run_container "$cloudflare"
run_container "$elsewhere"
nginx_ip=$(address_of "$nginx") || stop "Couldn't read the address of $nginx on $net: $nginx_ip"
cloudflare_ip=$(address_of "$cloudflare") || stop "Couldn't read the address of $cloudflare on $net: $cloudflare_ip"
# The shape of cPanel's list of Cloudflare's addresses, with one address instead of Cloudflare's 22 ranges
printf 'set_real_ip_from %s;\nreal_ip_header CF-Connecting-IP;\n' "$cloudflare_ip" > conf.d/includes-optional/cloudflare.conf
out=$(timeout -k 10 60 docker cp conf.d/. "$nginx:/etc/nginx/conf.d/" 2>&1) || stop "Couldn't copy conf.d into nginx's container: $out"
out=$(timeout -k 10 60 docker cp minerbot "$nginx:/etc/nginx/minerbot" 2>&1) || stop "Couldn't copy minerbot/ into nginx's container: $out"
out=$(timeout -k 10 60 docker cp njs "$nginx:/etc/nginx/njs" 2>&1) || stop "Couldn't copy njs/ into nginx's container: $out"
# The image has njs, which the stand-in's slow answer needs, but doesn't load it
out=$(inside 'printf "load_module modules/ngx_http_js_module.so;\n" | cat - /etc/nginx/nginx.conf > /tmp/nginx.conf \
    && cat /tmp/nginx.conf > /etc/nginx/nginx.conf' 2>&1) || stop "Couldn't load njs in nginx's container: $out"
# The socket's folder as on the server: only the dashboard's user (1654) may write it, and nginx's workers, here of the
# group nginx, may enter it. The set-group-ID bit gives the socket that group.
out=$(inside 'mkdir -p /run/minerbot-dashboard && chown 1654:nginx /run/minerbot-dashboard \
    && chmod 2750 /run/minerbot-dashboard' 2>&1) || stop "Couldn't make the socket's folder in nginx's container: $out"

# ---------- the checks
# Each check's function notes its own problems, and runs with || true, so one failed check never ends the run

pipelog_included || true
verdict "the file includes /etc/nginx/minerbot/pipelog.con[f] exactly twice, one for each server, and never names pipelog itself"
nginx_starts || true
verdict "nginx -t passes without a warning, and nginx starts, next to a reuseport default server, an account block with cPanel's listen lines and the parent domain's block"
[ -n "$started" ] || stop "nginx isn't running, so the other $((checks - made)) checks can't run."
http_level_kept_out || true
verdict "with the http level's gzip on and add_header, the dashboard's responses are neither compressed nor given that header, while the account's site gets both"
headers_replaced || true
verdict "from Cloudflare, on /admin and on Discord's callback, forged headers become the fixed Host, nginx's key, the visitor's address and https; X-Forwarded-Host, -Port and -Prefix, Forwarded and X-Real-IP are emptied; the Access token passes"
host_fixed || true
verdict "a Host with a port, in other letters, or beside an absolute address in the request line, reaches the dashboard as the fixed name"
others_refused || true
verdict "403 from outside Cloudflare, and from Cloudflare without CF-Connecting-IP, on 443 and on 80"
port_80_redirects || true
verdict "port 80 sends Cloudflare's visitors to https://bot.murderminershub.com with a 301 that keeps the query, whatever Host says"
other_sites_answer || true
verdict "the account's site and the default server still answer their own names"
parent_domain_kept || true
verdict "the parent domain's own site and every other name under it keep their own blocks on 80 and 443; the dashboard takes only its own name"
big_header_passes || true
verdict "a 12 KB response header passes (the 16k proxy buffer)"
slow_answer_passes || true
verdict "an answer after 8 seconds still reaches the visitor, on /admin and on Discord's callback in any letter case"
bodies_capped || true
verdict "a body of exactly 256 KB passes in memory, with no temporary-file warning, and one byte more gets 413, by Content-Length and chunked"
large_page_in_memory || true
verdict "a page far larger than the proxy buffers, to a visitor who pauses, arrives whole and is never written to a temporary file"
temp_file_warning_logged || true
verdict "with the body buffer at 16 KB, a 200 KB body's temporary-file warning reaches the dashboard's error log, so the two refusals above can fail"
access_log_plain || true
verdict "the access log has the visitor's address and the path, without the query, user agent, referer or cookies"
port_80_logged_plain || true
verdict "port 80's redirect is in the dashboard's access log, without the query, and nothing of the dashboard's reaches the http level's log"
dashboard_too_slow || true
verdict "a dashboard slower than proxy_read_timeout gets the same plain 503 as a stopped one, and Discord's callback still leaves nothing in the error log"
dashboard_down || true
verdict "with the dashboard's socket missing: a plain 503 with Retry-After; Discord's callback, in any letter case, leaves nothing in the error log, which still logs other addresses, near misses included"
socket_left_behind || true
verdict "with a socket nothing listens on, as a dashboard killed outright leaves it, or one nginx's workers can't open: the same plain 503, and Discord's callback still leaves nothing in the error log"
port_5080_left_alone || true
verdict "with the dashboard down, a process on 127.0.0.1:5080, where it used to listen, gets nothing, and the visitor gets the 503"
key_file_optional || true
verdict "without the proxy key's file, nginx -t still passes"
pipelog_conf_read || true
verdict "nginx reads pipelog.conf at the path the file includes: a pipelog line there, which this nginx can't load, fails nginx -t naming that file"
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

#!/usr/bin/env bash
# Checks the nginx procedures in deploy/common.sh with the real nginx, at the server's version, before they reach the
# server: nginx_change, nginx_remove and nginx_undo_cut_off, which setup-dashboard.sh runs as root and which could stop
# nginx from reloading for every site there if they went wrong; and nginx_look, which must never print a secret.
#
# They run as root in one container of nginx's Debian image, without a network, laid out like the server: nginx.conf's
# main context as the server has it, a default server with reuseport, an account's block with cPanel's listen lines, a
# stand-in for cPanel's Cloudflare list that names 127.0.0.2, a root-only cPanel include with made-up secrets, and a
# stand-in dashboard on 127.0.0.1:5080 that answers with the headers it got. The real common.sh and
# minerbot-dashboard.conf are in /opt/minerbot. /scripts/restartsrv_nginx runs nginx -s reload and counts its calls, and
# every call to nginx is written down on its way to the real binary. Requests as Cloudflare come from 127.0.0.2.
#
# The copy of minerbot-dashboard.conf that nginx gets leaves out its two "pipelog off;" lines, because cPanel's pipelog
# module isn't in nginx's image. nginx-test.sh checks that the file has them.
#
# Run it from Git Bash or Linux: bash deploy/tests/nginx-procedure-test.sh. It needs Docker. It prints one line per
# check, then "N of M passed", and exits 1 if any check failed. Its container is named mbtest-nginxproc-<its PID>, ends
# by itself after 5 minutes, and is removed when the run ends.
#
# The script runs in three ways: as above, on this machine; with --inside, in the container, where it makes the checks;
# and with --call <function> <arguments>, also in the container, where it runs one of common.sh's functions the way
# setup-dashboard.sh runs it: in a bash of its own, with set -Eeuo pipefail, umask 077 and the scripts' ERR trap.
set -Eeuo pipefail
umask 077

# The server's nginx version, in nginx's Debian image, which has bash, curl and openssl. Change it with the server's.
image=nginx:1.31.8
name=mbtest-nginxproc-$$
checks=9   # how many checks this script makes
made=0
passed=0
notes=""

host=bot.murderminershub.com
# The visitor's address, as Cloudflare's CF-Connecting-IP header gives it
visitor=203.0.113.7
# Made-up proxy keys, one for each set of files the checks hand to nginx_change
key1=1111111111111111111111111111111111111111111111111111111111111111
key2=2222222222222222222222222222222222222222222222222222222222222222
key3=3333333333333333333333333333333333333333333333333333333333333333
# Made-up secrets in a root-only include, like cPanel's cpanel-proxy-xt.conf. The second one's line names nothing
# nginx_look filters by name, so only the file's mode keeps it out.
cpanel_secret="made-up-cpanel-secret-4d1c"
other_secret="made-up-second-secret-82be"
# nginx.conf's main context as the server has it, which the test on its own must copy first, in this order
main_lines="include /etc/nginx/conf.d/modules/*.conf;
user nobody;"
standalone=/opt/minerbot/state/nginx-check/nginx.conf

# ---------- Reporting, as in nginx-test.sh

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

# ---------- In the container: one of common.sh's functions, run as setup-dashboard.sh runs it

call_one() {   # call_one <function> [<arguments>...]
    # shellcheck source=../common.sh
    source /opt/minerbot/common.sh
    trap 'stopped $? $LINENO' ERR
    cd "$home"
    "$@"
    printf '%s\n' "${nginx_result:-}" > /work/result
}

# ---------- In the container: helpers for the checks

put_file() {   # put_file <mode> <file>: writes the file from the input, with that mode
    cat > "$2"
    chmod "$1" "$2"
}

make_cert() {   # make_cert <folder> <name>: a self-signed certificate for the name, and its key
    install -d -m 755 "$1"
    timeout -k 5 60 openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 2 -subj "/CN=$2" \
        -addext "subjectAltName=DNS:$2" -keyout "$1/origin-key.pem" -out "$1/origin-cert.pem" 2> /dev/null
}

# The four files setup-dashboard.sh hands nginx_change, in a folder of their own
make_set() {   # make_set <folder> <certificate's folder> <proxy key>
    install -d -m 700 "$1"
    grep -vxE '[[:space:]]*pipelog off;[[:space:]]*' /opt/minerbot/minerbot-dashboard.conf \
        > "$1/minerbot-dashboard.conf"
    cp "$2/origin-cert.pem" "$2/origin-key.pem" "$1/"
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$3" > "$1/proxy-key.conf"
}

nginx_test() {   # the checks' own nginx -t with every site; prints nginx's message
    timeout -k 5 30 /usr/sbin/nginx -t -q 2>&1
}

reloads() {   # how many times cPanel's reload has run
    wc -l < /work/reloads
}

workers() {   # the PIDs of nginx's worker processes
    local master dir
    master=$(cat /run/nginx.pid 2> /dev/null) || return 0
    for dir in /proc/[0-9]*; do
        if grep -qx "PPid:[[:space:]]*$master" "$dir/status" 2> /dev/null; then
            printf '%s\n' "${dir#/proc/}"
        fi
    done
}

settle() {   # settle <PIDs of the workers before a reload>: waits up to 10 s until they have all exited
    local pid left
    for _ in $(seq 100); do
        left=""
        for pid in $1; do
            if [ -d "/proc/$pid" ]; then left=$pid; fi
        done
        if [ -z "$left" ]; then return 0; fi
        sleep 0.1
    done
    problem "a worker with the old files still ran 10 seconds after the reload"
}

reload_nginx() {   # the checks' own reload, which the count leaves out; it waits until the old workers are gone
    local old out
    old=$(workers)
    if ! out=$(timeout -k 5 30 /usr/sbin/nginx -s reload 2>&1); then
        problem "nginx -s reload failed" "$out"
        return 0
    fi
    settle "$old"
}

# Runs one of common.sh's functions as setup-dashboard.sh does, in a bash of its own. Sets said (what it printed),
# status (its exit code), result (its nginx_result) and reloaded (how many times it ran cPanel's reload). After a
# reload it waits until nginx's old workers are gone, so the next request meets the new files.
run_common() {   # run_common <function> [<arguments>...]
    local count old
    count=$(reloads)
    old=$(workers)
    rm -f /work/result
    status=0
    said=$(timeout -k 10 200 bash "$0" --call "$@" 2>&1) || status=$?
    result=$(cat /work/result 2> /dev/null) || result="none: it stopped before its end"
    reloaded=$(($(reloads) - count))
    if [ "$reloaded" -gt 0 ]; then
        settle "$old"
    fi
}

outcome() {   # outcome <result> <reloads>: what the last run_common must have given
    if [ "$status" != 0 ]; then
        problem "it stopped with exit code $status" "$said"
    elif [ "$result" != "$1" ]; then
        problem "its result was '$result', not '$1'" "$said"
    fi
    [ "$reloaded" = "$2" ] || problem "cPanel's reload ran $reloaded time(s), not $2"
}

calls_were() {   # calls_were <what> <line>...: the calls to nginx since the check began must be exactly these
    local what=$1 found
    shift
    found=$(cat /work/nginx-calls)
    [ "$found" = "$(printf '%s\n' "$@")" ] || problem "nginx's calls weren't $what" "$found"
}

request() {   # request <curl arguments...>: nginx's answer on 127.0.0.1:443, headers first, or curl's error
    timeout -k 5 20 curl -sSk -i --max-time 10 --resolve "$host:443:127.0.0.1" \
        --resolve "other.example:443:127.0.0.1" --resolve "unknown.example:443:127.0.0.1" "$@" 2>&1 || true
}
as_cloudflare() {   # as_cloudflare <path>: a visitor's request as Cloudflare passes it on, from the address in its list
    request --interface 127.0.0.2 -H "CF-Connecting-IP: $visitor" "https://$host$1"
}

served_print() {   # the SHA-256 fingerprint of the certificate nginx serves for the dashboard's name
    timeout -k 5 20 openssl s_client -connect 127.0.0.1:443 -servername "$host" < /dev/null 2> /dev/null \
        | timeout -k 5 20 openssl x509 -noout -fingerprint -sha256 2>&1 || true
}
file_print() {   # file_print <certificate>: its SHA-256 fingerprint
    timeout -k 5 20 openssl x509 -noout -fingerprint -sha256 -in "$1" 2>&1 || true
}

sha() {   # sha <file>: its SHA-256, or "missing"
    if [ -f "$1" ]; then
        sha256sum < "$1" | cut -d ' ' -f 1
    else
        echo missing
    fi
}

snapshot() {   # every folder and file under conf.d and /etc/nginx/minerbot, with its mode, and each file's SHA-256
    local dir
    for dir in /etc/nginx/conf.d /etc/nginx/minerbot; do
        if [ -d "$dir" ]; then
            find "$dir" -printf '%y %m %p\n' | LC_ALL=C sort
            find "$dir" -type f -exec sha256sum {} + | LC_ALL=C sort -k 2
        else
            echo "no $dir"
        fi
    done
}
same_as() {   # same_as <snapshot from before>: nginx's files must be as they were then, byte for byte, modes included
    local now
    now=$(snapshot)
    [ "$now" = "$1" ] || problem "nginx's files aren't as they were before, byte for byte" \
        "$(diff <(printf '%s\n' "$1") <(printf '%s\n' "$now"))"
}

# nginx's files as an earlier setup left them, then the checks' own reload: where most checks start
put_in_place() {   # put_in_place <set>
    local out
    rm -f /etc/nginx/conf.d/zz-*.conf /opt/minerbot/state/nginx-change
    rm -rf /etc/nginx/minerbot
    install -d -m 700 /etc/nginx/minerbot
    install -m 600 "$1/origin-cert.pem" "$1/origin-key.pem" "$1/proxy-key.conf" /etc/nginx/minerbot/
    install -m 644 "$1/minerbot-dashboard.conf" /etc/nginx/conf.d/minerbot-dashboard.conf
    out=$(nginx_test) || problem "nginx -t refused the files this check starts from" "$out"
    reload_nginx
}

# nginx's four files are the set's, byte for byte, with the modes setup gives them, and nothing is left beside them
installed_as() {   # installed_as <set>
    local name target mode
    for name in minerbot-dashboard.conf origin-cert.pem origin-key.pem proxy-key.conf; do
        target=/etc/nginx/minerbot/$name
        mode=600
        if [ "$name" = minerbot-dashboard.conf ]; then
            target=/etc/nginx/conf.d/$name
            mode=644
        fi
        [ "$(sha "$target")" = "$(sha "$1/$name")" ] || problem "$target isn't the new $name, byte for byte"
        [ "$(stat -c %a "$target" 2>&1)" = "$mode" ] || problem "$target's mode isn't $mode" "$(ls -l "$target" 2>&1)"
    done
    [ "$(stat -c %a /etc/nginx/minerbot 2>&1)" = 700 ] \
        || problem "/etc/nginx/minerbot's mode isn't 700" "$(ls -ld /etc/nginx/minerbot 2>&1)"
    [ "$(find /etc/nginx/minerbot -mindepth 1 -printf '%f\n' 2>&1 | LC_ALL=C sort | tr '\n' ' ')" \
        = "origin-cert.pem origin-key.pem proxy-key.conf " ] \
        || problem "/etc/nginx/minerbot holds more than its three files" "$(ls -lA /etc/nginx/minerbot 2>&1)"
    [ -z "$(find /etc/nginx/conf.d -name '.*')" ] \
        || problem "conf.d holds a temporary file" "$(find /etc/nginx/conf.d -name '.*')"
    [ ! -e /opt/minerbot/state/nginx-change ] || problem "the marker state/nginx-change is still there"
}

# ---------- In the container: the checks, in the order they run

first_install() {
    local answer
    : > /work/nginx-calls
    run_common nginx_change /work/first
    outcome reloaded 1
    calls_were "the test on its own with an absolute -c, then the test with every site, then one reload" \
        "-t -q -c $standalone" "-t -q" "-s reload"
    [ "$(head -n 2 "$standalone" 2>&1)" = "$main_lines" ] \
        || problem "the test on its own didn't start with nginx.conf's main context, in its order" \
            "$(cat "$standalone" 2>&1)"
    installed_as /work/first
    answer=$(request "https://$host/")
    expect "HTTP/1.1 403" "$answer" "a request from this server, which isn't Cloudflare"
    answer=$(as_cloudflare /admin)
    expect "HTTP/1.1 200" "$answer" "Cloudflare's request"
    expect "key=$key1 xff=$visitor " "$answer" "what the stand-in dashboard got"
}

main_context_copied() {
    local user out answer
    # nginx falls back to its compiled-in user when nginx.conf names none. The image's is nginx; the server's nginx.conf
    # names nobody. With the image's user gone, a test on its own without nginx.conf's user line can't pass (R6).
    user=$(/usr/sbin/nginx -V 2>&1 | tr ' ' '\n' | sed -n 's/^--user=//p')
    if [ -n "$user" ] && getent passwd "$user" > /dev/null; then
        out=$(userdel "$user" 2>&1) || problem "couldn't remove nginx's compiled-in user $user" "$out"
    fi
    if [ -z "$user" ] || getent passwd "$user" > /dev/null; then
        problem "nginx's compiled-in user ('$user') still exists, so this check would pass without the main context"
    fi
    run_common nginx_change /work/second
    outcome reloaded 1
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the new proxy key, at the stand-in dashboard"
}

refused_on_its_own() {
    local before answer
    make_set /work/typo /work/certs/a "$key2"
    sed -i 's/^\([[:space:]]*\)proxy_read_timeout 60s;/\1proxy_read_timout 60s;/' /work/typo/minerbot-dashboard.conf
    grep -q 'proxy_read_timout' /work/typo/minerbot-dashboard.conf || problem "the typo wasn't made"
    before=$(snapshot)
    : > /work/nginx-calls
    run_common nginx_change /work/typo
    outcome refused 0
    expect 'unknown directive "proxy_read_timout"' "$said" "nginx's own message"
    same_as "$before"
    calls_were "only the test on its own" "-t -q -c $standalone"
    [ ! -e /etc/nginx/minerbot/.staging ] || problem "the staging folder is still there"
    [ ! -e /opt/minerbot/state/nginx-change ] || problem "the marker state/nginx-change is still there"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "Cloudflare's request, which nginx still answers with the files it ran"
}

refused_with_every_site() {
    local before out answer
    # Starts without our conf.d file and proxy-key.conf, while another file defines our log_format's name (R7)
    put_in_place /work/second
    rm /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/minerbot/proxy-key.conf
    # shellcheck disable=SC2016 # nginx's variable
    printf 'log_format minerbot_dashboard '\''$remote_addr'\'';\n' | put_file 644 /etc/nginx/conf.d/zz-other.conf
    out=$(nginx_test) || problem "nginx -t refused the files this check starts from" "$out"
    reload_nginx
    before=$(snapshot)
    : > /work/nginx-calls
    run_common nginx_change /work/clash
    outcome refused 0
    expect 'duplicate "log_format" name "minerbot_dashboard"' "$said" "nginx's own message"
    same_as "$before"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] || problem "our conf.d file, which wasn't there before, is there"
    [ ! -e /etc/nginx/minerbot/proxy-key.conf ] || problem "proxy-key.conf, which wasn't there before, is there"
    out=$(nginx_test) || problem "nginx -t fails with the old files back" "$out"
    calls_were "the test on its own, the test with every site, and that test again with the old files back" \
        "-t -q -c $standalone" "-t -q" "-t -q"
    [ ! -e /etc/nginx/minerbot/.staging ] || problem "the staging folder is still there"
    [ ! -e /opt/minerbot/state/nginx-change ] || problem "the marker state/nginx-change is still there"
    answer=$(request https://other.example/)
    expect "account site" "$answer" "the account's own site"
    answer=$(request https://unknown.example/)
    expect "default server" "$answer" "an unknown name"
    rm -f /etc/nginx/conf.d/zz-other.conf
}

certificate_replaced() {
    local inode answer
    put_in_place /work/second
    inode=$(stat -c %i /etc/nginx/minerbot/origin-cert.pem)
    run_common nginx_change /work/replaced
    outcome reloaded 1
    installed_as /work/replaced
    [ "$(stat -c %i /etc/nginx/minerbot/origin-cert.pem 2>&1)" != "$inode" ] \
        || problem "the certificate was written over in place, not renamed in"
    [ "$(served_print)" = "$(file_print /work/replaced/origin-cert.pem)" ] \
        || problem "nginx serves another certificate than the new one" "$(served_print)"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key, at the stand-in dashboard"
}

removed_while_test_fails() {
    local out sum copy answer
    put_in_place /work/replaced
    rm /etc/nginx/minerbot/origin-cert.pem
    if out=$(nginx_test); then
        problem "nginx -t passed without our certificate, so this check proves nothing"
    else
        expect "origin-cert.pem" "$out" "nginx -t's message before the removal"
    fi
    sum=$(sha /etc/nginx/conf.d/minerbot-dashboard.conf)
    run_common nginx_remove
    outcome removed 1
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] || problem "our file is still in conf.d"
    out=$(nginx_test) || problem "nginx -t fails after the removal" "$out"
    # The backups' folders are named by the time, so the newest is the last
    copy=$(find /opt/minerbot/state/nginx-backup -mindepth 1 -maxdepth 1 -type d | LC_ALL=C sort | tail -n 1)
    [ "$(sha "$copy/minerbot-dashboard.conf")" = "$sum" ] \
        || problem "the newest backup doesn't hold our file, byte for byte"
    expect "Its copy: ${copy#/opt/minerbot/}/" "$said" "the message"
    [ ! -e /opt/minerbot/state/nginx-change ] || problem "the marker state/nginx-change is still there"
    # README.txt's next step
    out=$(rm -r /etc/nginx/minerbot 2>&1) || problem "rm -r /etc/nginx/minerbot failed" "$out"
    out=$(nginx_test) || problem "nginx -t fails without /etc/nginx/minerbot" "$out"
    reload_nginx
    answer=$(request https://other.example/)
    expect "account site" "$answer" "the account's own site"
    answer=$(request https://unknown.example/)
    expect "default server" "$answer" "an unknown name"
    answer=$(request "https://$host/")
    expect "default server" "$answer" "the dashboard's name, which only the default server answers now"
}

remove_finds_other_file() {
    local out before
    put_in_place /work/replaced
    printf 'this_is_not_a_directive on;\n' | put_file 644 /etc/nginx/conf.d/zz-broken.conf
    if out=$(nginx_test); then
        problem "nginx -t passed with the broken file, so this check proves nothing"
    else
        expect "zz-broken.conf" "$out" "nginx -t's message before the removal"
    fi
    before=$(snapshot)
    run_common nginx_remove
    outcome other-file 0
    same_as "$before"
    expect "the problem is in another file" "$said" "the message"
    [ ! -e /opt/minerbot/state/nginx-change ] || problem "the marker state/nginx-change is still there"
    rm -f /etc/nginx/conf.d/zz-broken.conf
}

cut_off_undone() {
    local before out answer
    put_in_place /work/replaced
    before=$(snapshot)
    # The real nginx_change, cut off as a crash would, just as it renames the new key into place: this mv, which comes
    # before the real one on common.sh's PATH for this one run, kills the run that called it. The marker and a complete
    # backup are there, the new certificate is in place beside the old key, and the other new files are still in the
    # staging folder.
    put_file 755 /usr/local/bin/mv << 'EOF'
#!/bin/sh
for last; do :; done
if [ "$last" = /etc/nginx/minerbot/origin-key.pem ]; then
    kill -KILL "$PPID"
    exit 1
fi
exec /usr/bin/mv "$@"
EOF
    run_common nginx_change /work/clash
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "nginx_change wasn't cut off (exit code $status)" "$said"
    [ "$reloaded" = 0 ] || problem "the cut-off change ran cPanel's reload $reloaded time(s)"
    [ -e /opt/minerbot/state/nginx-change ] || problem "the cut-off change left no marker"
    if out=$(nginx_test); then
        problem "nginx -t passed after the cut, so this check proves nothing"
    else
        expect "key values mismatch" "$out" "nginx -t's message after the cut"
    fi
    run_common nginx_undo_cut_off
    outcome undone 1
    same_as "$before"
    out=$(nginx_test) || problem "nginx -t fails after the undo" "$out"
    [ ! -e /opt/minerbot/state/nginx-change ] || problem "the marker state/nginx-change is still there"
    [ "$(served_print)" = "$(file_print /work/replaced/origin-cert.pem)" ] \
        || problem "nginx doesn't serve the certificate from before the change" "$(served_print)"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key from before the change, at the stand-in dashboard"
}

look_keeps_secrets() {
    local out
    put_in_place /work/replaced
    # nginx -T runs as root and prints every included file, root-only ones too
    out=$(timeout -k 5 30 /usr/sbin/nginx -T 2>&1)
    expect "$cpanel_secret" "$out" "nginx -T itself, which prints the root-only cPanel include"
    expect "$other_secret" "$out" "nginx -T itself, which prints the root-only cPanel include"
    expect "$key2" "$out" "nginx -T itself, which prints proxy-key.conf"
    run_common nginx_look
    outcome "" 0
    refuse "$cpanel_secret" "$said" "nginx_look"
    refuse "$other_secret" "$said" "nginx_look"
    refuse "$key2" "$said" "nginx_look"
    expect "/etc/nginx/conf.d/ea-nginx.conf: client_max_body_size 128m;" "$said" "nginx_look's http-level lines"
    refuse "proxy_pass" "$said" "nginx_look, which leaves out what server blocks hold"
}

# ---------- In the container: the layout, then the checks

inside_container() {
    local out
    trap 'stop "The container'\''s setup failed at line $LINENO."' ERR
    install -d -m 755 /etc/nginx/conf.d/modules /etc/nginx/conf.d/users /etc/nginx/conf.d/includes-optional \
        /etc/nginx/other-sites /scripts
    install -d -m 700 /work/certs

    # nginx.conf as the server has it. The server has a group named nobody, which "user nobody;" needs; Debian's is
    # called nogroup.
    getent group nobody > /dev/null || groupadd nobody
    put_file 644 /etc/nginx/nginx.conf << 'EOF'
include /etc/nginx/conf.d/modules/*.conf;
user nobody;
worker_processes 1;
error_log /var/log/nginx/error.log warn;
pid /var/run/nginx.pid;

events {
    worker_connections 1024;
}

http {
    include /etc/nginx/mime.types;
    default_type application/octet-stream;
    log_format main '$remote_addr - $remote_user [$time_local] "$request" $status $body_bytes_sent';
    access_log /var/log/nginx/access.log main;
    sendfile on;
    keepalive_timeout 65;
    server_tokens off;
    include /etc/nginx/conf.d/*.conf;
}
EOF
    # The server's modules folder loads cPanel's pipelog module, which isn't in this image. Another one stands in.
    printf 'load_module modules/ngx_http_js_module.so;\n' | put_file 644 /etc/nginx/conf.d/modules/stand-in.conf
    # The image links its two logs to its output, which would keep a command's output open as long as nginx runs
    rm -f /var/log/nginx/access.log /var/log/nginx/error.log

    # Like cPanel's default.conf, an account's block with cPanel's exact listen lines, and cPanel's http-level settings
    put_file 644 /etc/nginx/conf.d/default.conf << 'EOF'
server {
    listen 80 default_server reuseport;
    listen [::]:80 default_server reuseport;
    listen 443 ssl default_server reuseport;
    listen [::]:443 ssl default_server reuseport;
    ssl_certificate /etc/nginx/other-sites/origin-cert.pem;
    ssl_certificate_key /etc/nginx/other-sites/origin-key.pem;
    return 404 "default server\n";
}
EOF
    printf 'include /etc/nginx/conf.d/users/*.conf;\n' | put_file 644 /etc/nginx/conf.d/users.conf
    put_file 644 /etc/nginx/conf.d/users/acct.conf << 'EOF'
server {
    listen 80;
    listen [::]:80;
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 off;
    server_name other.example;
    ssl_certificate /etc/nginx/other-sites/origin-cert.pem;
    ssl_certificate_key /etc/nginx/other-sites/origin-key.pem;
    include conf.d/includes-optional/cloudflare.conf;
    default_type text/plain;
    return 200 "account site\n";
}
EOF
    printf 'client_max_body_size 128m;\nssl_protocols TLSv1.2 TLSv1.3;\n' | put_file 644 /etc/nginx/conf.d/ea-nginx.conf
    # The shape of cPanel's list of Cloudflare's addresses, with one address instead of Cloudflare's 22 ranges
    printf 'set_real_ip_from 127.0.0.2;\nreal_ip_header CF-Connecting-IP;\n' \
        | put_file 644 /etc/nginx/conf.d/includes-optional/cloudflare.conf
    # A root-only include with secrets, as cPanel's cpanel-proxy-xt.conf is, and a block that includes it
    printf 'proxy_set_header cPanel-localhost "%s";\nproxy_set_header X-Made-Up "%s";\n' \
        "$cpanel_secret" "$other_secret" | put_file 600 /etc/nginx/conf.d/includes-optional/cpanel-proxy-xt.conf
    put_file 644 /etc/nginx/conf.d/cpanel-proxy-non-ssl.conf << 'EOF'
server {
    listen 80;
    listen [::]:80;
    server_name cpanel.other.example;
    location / {
        include conf.d/includes-optional/cpanel-proxy-xt.conf;
        return 200 "cPanel's service\n";
    }
}
EOF
    # The dashboard, played by nginx: it answers with what it was sent
    put_file 644 /etc/nginx/conf.d/fake-dashboard.conf << 'EOF'
server {
    listen 127.0.0.1:5080;
    location / {
        default_type text/plain;
        return 200 "host=$http_host key=$http_x_minerbot_proxy_key xff=$http_x_forwarded_for proto=$http_x_forwarded_proto uri=$request_uri\n";
    }
}
EOF

    # Certificates: one for the other sites, and three for the dashboard's name
    make_cert /etc/nginx/other-sites other.example
    make_cert /work/certs/a "$host"
    make_cert /work/certs/b "$host"
    make_cert /work/certs/c "$host"
    # The sets of files the checks hand nginx_change, as setup-dashboard.sh writes them
    make_set /work/first /work/certs/a "$key1"
    make_set /work/second /work/certs/a "$key2"
    make_set /work/clash /work/certs/c "$key3"
    make_set /work/replaced /work/certs/b "$key2"

    # cPanel's reload, which counts its calls, and a note of every call to nginx on its way to the real binary. Both
    # are found through common.sh's PATH, which starts with /usr/local/sbin.
    put_file 755 /scripts/restartsrv_nginx << 'EOF'
#!/bin/sh
echo "$*" >> /work/reloads && nginx -s reload
EOF
    put_file 755 /usr/local/sbin/nginx << 'EOF'
#!/bin/sh
echo "$*" >> /work/nginx-calls
exec /usr/sbin/nginx "$@"
EOF
    : > /work/reloads

    if ! out=$(nginx_test); then
        stop "nginx -t refused the files the checks start from: $out"
    fi
    # nginx starts its master in the background. Its output goes to a file, so nothing waits for the master to end.
    if ! timeout -k 5 30 /usr/sbin/nginx > /work/nginx.out 2>&1; then
        stop "nginx didn't start: $(cat /work/nginx.out)"
    fi
    out=$(request https://other.example/)
    grep -qF "account site" <<< "$out" || stop "nginx doesn't answer for the account's site: $out"
    trap - ERR

    first_install || true
    verdict "1. A first install: the test on its own with nginx.conf's main context and an absolute -c, then the test with every site, then one reload. A request from this server gets 403, and Cloudflare's reaches the dashboard with the key. The modes are 700, 600 and 644."
    main_context_copied || true
    verdict "2. With nginx's compiled-in user gone, the test on its own still passes, because nginx.conf's user line comes first (R6)."
    refused_on_its_own || true
    verdict "3. A file nginx refuses on its own changes nothing: conf.d and /etc/nginx/minerbot are the same by SHA-256, the staging folder and the marker are gone, and nginx wasn't reloaded."
    refused_with_every_site || true
    verdict "4. A file that passes on its own but clashes with another file (R7): every file back byte for byte, the ones that weren't there gone again, nginx -t passes, no reload, and the other sites answer."
    certificate_replaced || true
    verdict "5. A new certificate arrives by rename, with one reload, and nginx serves it for the name."
    removed_while_test_fails || true
    verdict "6. --remove-nginx while nginx -t fails for our missing certificate: our file goes, with a copy kept, nginx -t passes, one reload. Then without /etc/nginx/minerbot, nginx -t passes, nginx reloads and the other sites answer."
    remove_finds_other_file || true
    verdict "7. --remove-nginx while another file is broken: our file is back byte for byte, nginx wasn't reloaded, and the message says the problem is in another file."
    cut_off_undone || true
    verdict "8. A change cut off between two renames (the new certificate beside the old key) is undone: every file back byte for byte, nginx -t passes, one reload, and the marker is gone."
    look_keeps_secrets || true
    verdict "9. nginx_look prints the http-level lines, but neither the proxy key nor the root-only cPanel include's secrets, which nginx -T itself prints."

    echo "$passed of $checks passed"
    if [ "$made" != "$checks" ]; then
        echo "This script made $made checks, but checks= at its top says $checks. Correct that count."
        exit 1
    fi
    if [ "$passed" != "$checks" ]; then
        exit 1
    fi
    exit 0
}

# ---------- On this machine: the container

cleanup() {
    for _ in 1 2 3 4 5; do
        timeout -k 10 60 docker rm -f -v "$name" > /dev/null 2>&1 || true
        if ! timeout -k 10 30 docker container inspect "$name" > /dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    echo "The container $name is still there. Remove it with: docker rm -f -v $name"
}

copy_in() {   # copy_in <file here> <path in the container>: through docker exec's input, which needs no path conversion
    local out
    # shellcheck disable=SC2016 # the $ are for the container's shell
    out=$(timeout -k 10 60 docker exec -i "$name" sh -c 'mkdir -p "${1%/*}" && cat > "$1"' sh "$2" < "$1" 2>&1) \
        || stop "Couldn't copy $1 into $name: $out"
}

on_host() {
    local here deploy out line status=""
    # Git Bash would otherwise turn the container's paths below into Windows paths
    export MSYS_NO_PATHCONV=1
    here=$(cd "$(dirname "$0")" && pwd)
    deploy=$(cd "$here/.." && pwd)
    trap 'code=$?; cleanup || true; exit "$code"' EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP

    echo "Checking the nginx procedures in deploy/common.sh with the real nginx in $image"
    [ -f "$deploy/common.sh" ] || stop "There's no file at $deploy/common.sh."
    [ -f "$deploy/minerbot-dashboard.conf" ] || stop "There's no file at $deploy/minerbot-dashboard.conf."
    timeout -k 10 30 docker version > /dev/null 2>&1 || stop "Docker isn't running, or doesn't answer."
    if ! timeout -k 10 30 docker image inspect "$image" > /dev/null 2>&1; then
        timeout -k 10 300 docker pull -q "$image" > /dev/null || stop "Couldn't pull $image within 5 minutes."
    fi
    # Its main process is sleep 300, so even a run killed before it can clean up leaves nothing running after 5
    # minutes. IPv6 is on, as the server's blocks listen on [::] too.
    out=$(timeout -k 10 120 docker run -d --rm --name "$name" --network none \
        --sysctl net.ipv6.conf.all.disable_ipv6=0 "$image" sleep 300 2>&1) \
        || stop "Couldn't start the container $name: $out"
    copy_in "$0" /work/nginx-procedure-test.sh
    copy_in "$deploy/common.sh" /opt/minerbot/common.sh
    copy_in "$deploy/minerbot-dashboard.conf" /opt/minerbot/minerbot-dashboard.conf
    echo "The real common.sh and minerbot-dashboard.conf are in /opt/minerbot in $name."

    # The checks print their own lines and count. This counts the ok lines as well, for when the limit stops them.
    while IFS= read -r -t 300 line; do
        case $line in
            "mbtest exit code "*) status=${line#mbtest exit code } ;;
            *)
                printf '%s\n' "$line"
                if [[ $line == "ok: "* ]]; then passed=$((passed + 1)); fi
                ;;
        esac
    done < <(
        code=0
        timeout -k 10 270 docker exec "$name" bash /work/nginx-procedure-test.sh --inside 2>&1 || code=$?
        echo "mbtest exit code $code"
    )
    case $status in
        0) exit 0 ;;
        1) exit 1 ;;
        124 | 137) stop "The checks didn't finish within 270 seconds." ;;
        *) stop "The checks ended with exit code ${status:-unknown}." ;;
    esac
}

case ${1:-} in
    --call)
        shift
        call_one "$@"
        ;;
    --inside) inside_container ;;
    "") on_host ;;
    *)
        echo "Usage: bash deploy/tests/nginx-procedure-test.sh" >&2
        exit 1
        ;;
esac

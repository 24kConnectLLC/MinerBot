#!/usr/bin/env bash
# Checks the nginx procedures in deploy/common.sh with the real nginx, at the server's version, before they reach the
# server: nginx_change, nginx_remove and nginx_undo_cut_off, which setup-dashboard.sh runs as root and which could stop
# nginx from reloading for every site there if they went wrong; and nginx_look, which must never print a secret.
#
# They run as root in one container of nginx's Debian image, without a network, laid out like the server: nginx.conf's
# main context as the server has it, a default server with reuseport, an account's block with cPanel's listen lines, a
# stand-in for cPanel's Cloudflare list that names 127.0.0.2, a root-only cPanel include with made-up secrets, and a
# stand-in dashboard on the dashboard's socket, /run/minerbot-dashboard/dashboard.sock, in a folder laid out as on the
# server, that answers with the headers it got. The real common.sh and minerbot-dashboard.conf are in /opt/minerbot.
# /scripts/restartsrv_nginx runs nginx -s reload and counts its calls, and every call to nginx is written down on its
# way to the real binary. Requests as Cloudflare come from 127.0.0.2.
#
# nginx gets minerbot-dashboard.conf as it is. Its blocks include /etc/nginx/minerbot/pipelog.con[f], which nginx_change
# writes empty here, since cPanel's pipelog module isn't in nginx's image and the modules folder has no load line for it,
# as on a server with WHM's piped logging off. cPanel's folder of config scripts is there, for the hook nginx_change
# installs. procps isn't in the image, so a stand-in pgrep lists the master's workers from /proc, as common.sh asks.
#
# Run it from Git Bash or Linux: bash deploy/tests/nginx-procedure-test.sh. It needs Docker. It prints one line per
# check, then "N of M passed", and exits 1 if any check failed. Its container is named mbtest-nginxproc-<its PID>, ends
# by itself after 7 minutes, and is removed when the run ends.
#
# The script runs in three ways: as above, on this machine; with --inside, in the container, where it makes the checks;
# and with --call <function> <arguments>, also in the container, where it runs one of common.sh's functions the way
# setup-dashboard.sh runs it: in a bash of its own, with set -Eeuo pipefail, umask 077 and the scripts' ERR trap.
set -Eeuo pipefail
umask 077

# The server's nginx version, in nginx's Debian image, which has bash, curl and openssl. Change it with the server's.
image=nginx:1.31.8
name=mbtest-nginxproc-$$
checks=23   # how many checks this script makes
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
# nginx_look filters by name, so only the file's mode keeps it out. So do the others, in root-only includes whose paths
# nginx_look must read whole: one with a space in it, one outside /etc/nginx, and one nginx -T names with "..". The last
# two are in world-readable files inside a folder only root can enter, which only that folder keeps out: one the include
# names, and one it names through a world-readable link outside the folder.
cpanel_secret="made-up-cpanel-secret-4d1c"
other_secret="made-up-second-secret-82be"
spaced_secret="made-up-spaced-secret-3a7e"
outside_secret="made-up-outside-secret-5e0a"
spelled_secret="made-up-spelled-secret-a71f"
foldered_secret="made-up-foldered-secret-77c2"
linked_secret="made-up-linked-secret-9d40"
# nginx.conf's main context as the server has it, which the test on its own must copy first, in this order
main_lines="include /etc/nginx/conf.d/modules/*.conf;
user nobody;"
standalone=/opt/minerbot/state/nginx-check/nginx.conf
marker=/opt/minerbot/state/nginx-change
# The program cPanel runs each time it rebuilds nginx's global files, which nginx_change installs
hook=/etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog

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
    cp /opt/minerbot/minerbot-dashboard.conf "$1/minerbot-dashboard.conf"
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

quiet() {   # waits up to 10 s until no worker of an earlier reload is still shutting down, as after two reloads
    for _ in $(seq 100); do
        # The [n] keeps grep from finding its own arguments
        if ! grep -qs 'worker process is shutting dow[n]' /proc/[0-9]*/cmdline; then return 0; fi
        sleep 0.1
    done
    problem "a worker of an earlier reload was still shutting down 10 seconds later"
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

# As a reboot after a crash or a power loss would: the master stops, and nginx starts again from the files on disk. TERM
# goes to the master itself, since nginx -s stop reads the files first and gives up when nginx -t would fail.
restart_nginx() {
    local master
    if master=$(cat /run/nginx.pid 2> /dev/null); then
        kill -TERM "$master" 2> /dev/null || true
        for _ in $(seq 100); do
            if [ ! -d "/proc/$master" ]; then break; fi
            sleep 0.1
        done
        [ ! -d "/proc/$master" ] || problem "nginx's master didn't stop within 10 seconds of TERM"
    fi
    if ! timeout -k 5 30 /usr/sbin/nginx > /work/nginx.out 2>&1; then
        problem "nginx didn't start again from the files on disk" "$(cat /work/nginx.out)"
    fi
}
nginx_running() {   # starts nginx when a check left it stopped, so the checks after it can run
    if [ ! -s /run/nginx.pid ] || [ ! -d "/proc/$(cat /run/nginx.pid)" ]; then
        timeout -k 5 30 /usr/sbin/nginx > /work/nginx.out 2>&1 || problem "nginx didn't start" "$(cat /work/nginx.out)"
    fi
}

# Runs one of common.sh's functions as setup-dashboard.sh does, in a bash of its own. Sets said (what it printed),
# status (its exit code), result (its nginx_result) and reloaded (how many times it ran cPanel's reload). After a
# reload it waits until nginx's old workers are gone, so the next request meets the new files; with unsettled=yes, for a
# reload nginx's master refused, which keeps them, it doesn't wait.
run_common() {   # run_common <function> [<arguments>...]
    local count old
    count=$(reloads)
    old=$(workers)
    rm -f /work/result
    status=0
    said=$(timeout -k 10 200 bash "$0" --call "$@" 2>&1) || status=$?
    result=$(cat /work/result 2> /dev/null) || result="none: it stopped before its end"
    reloaded=$(($(reloads) - count))
    if [ "$reloaded" -gt 0 ] && [ "${unsettled:-}" != yes ]; then
        settle "$old"
        quiet
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

folder_state() {   # folder_state <folder>: every folder and file in it, with its mode, and each file's SHA-256
    if [ -d "$1" ]; then
        find "$1" -printf '%y %m %p\n' | LC_ALL=C sort
        find "$1" -type f -exec sha256sum {} + | LC_ALL=C sort -k 2
    else
        echo "no $1"
    fi
}
snapshot() {   # every folder and file under conf.d and /etc/nginx/minerbot, with its mode, and each file's SHA-256
    folder_state /etc/nginx/conf.d
    folder_state /etc/nginx/minerbot
}
same_as() {   # same_as <snapshot from before>: nginx's files must be as they were then, byte for byte, modes included
    local now
    now=$(snapshot)
    [ "$now" = "$1" ] || problem "nginx's files aren't as they were before, byte for byte" \
        "$(diff <(printf '%s\n' "$1") <(printf '%s\n' "$now"))"
}

backup_list() {   # the folders in state/nginx-backup, by name
    find /opt/minerbot/state/nginx-backup -mindepth 1 -maxdepth 1 -printf '%f\n' 2> /dev/null | LC_ALL=C sort
}

# nginx's files as an earlier setup left them, then the checks' own reload: where most checks start
put_in_place() {   # put_in_place <set>
    local out
    rm -f /etc/nginx/conf.d/zz-*.conf /etc/nginx/conf.d/aa-*.conf "$marker"
    rm -rf /etc/nginx/minerbot
    install -d -m 700 /etc/nginx/minerbot
    install -m 600 "$1/origin-cert.pem" "$1/origin-key.pem" "$1/proxy-key.conf" /etc/nginx/minerbot/
    install -m 644 "$1/minerbot-dashboard.conf" /etc/nginx/conf.d/minerbot-dashboard.conf
    out=$(nginx_test) || problem "nginx -t refused the files this check starts from" "$out"
    reload_nginx
}

# A server without our files, as before a first install
nothing_in_place() {
    local out
    rm -f /etc/nginx/conf.d/minerbot-dashboard.conf /etc/nginx/conf.d/zz-*.conf /etc/nginx/conf.d/aa-*.conf "$marker"
    rm -rf /etc/nginx/minerbot
    install -d -m 700 /etc/nginx/minerbot
    out=$(nginx_test) || problem "nginx -t refused the files this check starts from" "$out"
    reload_nginx
}

# The next mv into that path kills the run that called it, as a crash or a power loss would: this mv comes before the
# real one on common.sh's PATH. With "restore", only the mv of a copy nginx_restore made beside its target; with
# "pipelog", only nginx_pipelog_conf's mv of the text cPanel's setting needs.
cut_at() {   # cut_at <path> [restore | pipelog]
    local when="[ \"\$last\" = $1 ]"
    if [ -n "${2:-}" ]; then
        # shellcheck disable=SC2016 # the $ are for the stand-in's shell
        when+=' && case "$*" in *.minerbot-'"$2"'.*) true ;; *) false ;; esac'
    fi
    # shellcheck disable=SC2016 # the $ are for the stand-in's shell
    printf '#!/bin/sh\nfor last; do :; done\nif %s; then\n    kill -KILL "$PPID"\n    exit 1\nfi\nexec /usr/bin/mv "$@"\n' \
        "$when" | put_file 755 /usr/local/bin/mv
}

# nginx's four files are the set's, byte for byte, with the modes setup gives them, and nothing is left beside them but
# pipelog.conf, empty here since this nginx doesn't load cPanel's pipelog module, and cPanel's hook that keeps it so
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
        = "origin-cert.pem origin-key.pem pipelog.conf proxy-key.conf " ] \
        || problem "/etc/nginx/minerbot doesn't hold exactly its four files" "$(ls -lA /etc/nginx/minerbot 2>&1)"
    [ -f /etc/nginx/minerbot/pipelog.conf ] && [ ! -s /etc/nginx/minerbot/pipelog.conf ] \
        && [ "$(stat -c %a /etc/nginx/minerbot/pipelog.conf 2>&1)" = 600 ] \
        || problem "pipelog.conf isn't empty and 600, though this nginx doesn't load the pipelog module" \
            "$(ls -l /etc/nginx/minerbot/pipelog.conf 2>&1; cat /etc/nginx/minerbot/pipelog.conf 2>&1)"
    [ "$(stat -c '%a %U' "$hook" 2>&1)" = "755 root" ] || problem "cPanel's hook isn't there, 755 and root's" "$(ls -l "$hook" 2>&1)"
    [ -z "$(find /etc/nginx/conf.d -name '.*')" ] \
        || problem "conf.d holds a temporary file" "$(find /etc/nginx/conf.d -name '.*')"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
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
    local before backups answer
    make_set /work/typo /work/certs/a "$key2"
    sed -i 's/^\([[:space:]]*\)proxy_read_timeout 60s;/\1proxy_read_timout 60s;/' /work/typo/minerbot-dashboard.conf
    grep -q 'proxy_read_timout' /work/typo/minerbot-dashboard.conf || problem "the typo wasn't made"
    before=$(snapshot)
    backups=$(backup_list)
    : > /work/nginx-calls
    run_common nginx_change /work/typo
    outcome refused 0
    expect 'unknown directive "proxy_read_timout"' "$said" "nginx's own message"
    same_as "$before"
    calls_were "only the test on its own" "-t -q -c $standalone"
    [ ! -e /etc/nginx/minerbot/.staging ] || problem "the staging folder is still there"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    # Only new folders count: with three backups there already, the refused run's own one pushes out the oldest
    [ -z "$(LC_ALL=C comm -13 <(printf '%s\n' "$backups") <(backup_list))" ] \
        || problem "the refused change left its backup folder in state/nginx-backup" "$(backup_list)"
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
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
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
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
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
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    rm -f /etc/nginx/conf.d/zz-broken.conf
}

cut_off_undone() {
    local before out answer
    put_in_place /work/replaced
    before=$(snapshot)
    # The real nginx_change, cut off as a crash would, just as it renames the new key into place. The marker and a
    # complete backup are there, the new certificate is in place beside the old key, and the other new files are still
    # in the staging folder.
    cut_at /etc/nginx/minerbot/origin-key.pem
    run_common nginx_change /work/clash
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "nginx_change wasn't cut off (exit code $status)" "$said"
    [ "$reloaded" = 0 ] || problem "the cut-off change ran cPanel's reload $reloaded time(s)"
    [ -e "$marker" ] || problem "the cut-off change left no marker"
    # Our conf.d file is out while the certificate and key don't belong together, so nginx -t passes
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] \
        || problem "our conf.d file was in place beside a certificate and key that don't belong together"
    out=$(nginx_test) || problem "nginx -t fails after the cut" "$out"
    # A crash or a power loss is followed by a boot: nginx must start, and the other sites answer
    restart_nginx
    answer=$(request https://other.example/)
    expect "account site" "$answer" "the account's own site, after nginx started again"
    run_common nginx_undo_cut_off
    outcome undone 1
    same_as "$before"
    out=$(nginx_test) || problem "nginx -t fails after the undo" "$out"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    [ "$(served_print)" = "$(file_print /work/replaced/origin-cert.pem)" ] \
        || problem "nginx doesn't serve the certificate from before the change" "$(served_print)"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key from before the change, at the stand-in dashboard"
    nginx_running
}

look_keeps_secrets() {
    local out secret
    put_in_place /work/replaced
    # Root-only includes that only their mode keeps out, whose paths the look must read whole: one with a space, one
    # outside /etc/nginx, and one nginx -T names with "..". And a world-readable one with a space in its path, whose
    # lines the look prints under its whole path.
    install -d -m 755 /srv/outside /etc/nginx/spelled
    printf 'proxy_set_header X-Spaced-Made-Up "%s";\n' "$spaced_secret" \
        | put_file 600 "/etc/nginx/conf.d/includes-optional/cp extra.conf"
    printf 'proxy_set_header X-Outside-Made-Up "%s";\n' "$outside_secret" | put_file 600 /srv/outside/private.conf
    printf 'proxy_set_header X-Spelled-Made-Up "%s";\n' "$spelled_secret" | put_file 600 /etc/nginx/spelled/private.conf
    printf 'client_body_timeout 33s;\n' | put_file 644 "/etc/nginx/conf.d/includes-optional/open extra.conf"
    # World-readable files in a folder only root can enter, which only root can read whatever their own modes: one the
    # include names, in a folder open to anyone inside it, so the look must check every folder above a file and not only
    # its own; and one it names through a world-readable link outside that folder
    install -d -m 700 /srv/private-only
    install -d -m 755 /srv/private-only/open
    printf 'proxy_set_header X-Foldered-Made-Up "%s";\n' "$foldered_secret" \
        | put_file 644 /srv/private-only/open/headers.conf
    printf 'proxy_set_header X-Linked-Made-Up "%s";\n' "$linked_secret" | put_file 644 /srv/private-only/linked.conf
    ln -sfn /srv/private-only/linked.conf /etc/nginx/conf.d/includes-optional/linked.conf
    put_file 644 /etc/nginx/conf.d/zz-look.conf << 'EOF'
include "/etc/nginx/conf.d/includes-optional/cp extra.conf";
include /srv/outside/private.conf;
include /etc/nginx/conf.d/../spelled/private.conf;
include "/etc/nginx/conf.d/includes-optional/open extra.conf";
include /srv/private-only/open/headers.conf;
include /etc/nginx/conf.d/includes-optional/linked.conf;
EOF
    out=$(nginx_test) || problem "nginx -t refused the includes" "$out"
    # nginx -T runs as root and prints every included file, root-only ones too
    out=$(timeout -k 5 30 /usr/sbin/nginx -T 2>&1)
    expect "$cpanel_secret" "$out" "nginx -T itself, which prints the root-only cPanel include"
    expect "$other_secret" "$out" "nginx -T itself, which prints the root-only cPanel include"
    expect "$key2" "$out" "nginx -T itself, which prints proxy-key.conf"
    expect "$spaced_secret" "$out" "nginx -T itself, which prints the root-only include with a space in its path"
    expect "$outside_secret" "$out" "nginx -T itself, which prints the root-only include outside /etc/nginx"
    expect "$spelled_secret" "$out" "nginx -T itself, which prints the root-only include named with .."
    expect "$foldered_secret" "$out" "nginx -T itself, which prints the world-readable include in a root-only folder"
    expect "# configuration file /etc/nginx/conf.d/includes-optional/linked.conf:" "$out" \
        "nginx -T itself, which names the link, not the file in the root-only folder it points to"
    expect "$linked_secret" "$out" "nginx -T itself, which prints the file the link points to"
    expect "# configuration file /etc/nginx/mime.types:" "$out" "nginx -T itself, which prints mime.types' types block"
    run_common nginx_look
    outcome "" 0
    for secret in "$cpanel_secret" "$other_secret" "$key2" "$spaced_secret" "$outside_secret" "$spelled_secret" \
        "$foldered_secret" "$linked_secret"; do
        refuse "$secret" "$said" "nginx_look"
    done
    expect "/etc/nginx/conf.d/ea-nginx.conf: client_max_body_size 128m;" "$said" "nginx_look's http-level lines"
    expect "/etc/nginx/conf.d/includes-optional/open extra.conf: client_body_timeout 33s;" "$said" \
        "nginx_look, for a world-readable include with a space in its path"
    refuse "proxy_pass" "$said" "nginx_look, which leaves out what server blocks hold"
    # Every line the look prints starts with its file and ": ", so this is any line of mime.types' types block
    refuse "/etc/nginx/mime.types: " "$said" "nginx_look, which leaves out types blocks"
    # /etc/nginx/minerbot is left out by its path alone: a world-readable proxy-key.conf whose key sits on a line of its
    # own, which names nothing the look filters by name, still stays out, with the folder opened to anyone
    chmod 755 /etc/nginx/minerbot
    printf 'proxy_set_header X-MinerBot-Proxy-Key\n    "%s";\n' "$key2" | put_file 644 /etc/nginx/minerbot/proxy-key.conf
    out=$(nginx_test) || problem "nginx -t refused the two-line proxy-key.conf" "$out"
    out=$(timeout -k 5 30 /usr/sbin/nginx -T 2>&1)
    expect "    \"$key2\";" "$out" "nginx -T itself, which prints the world-readable two-line proxy-key.conf"
    run_common nginx_look
    outcome "" 0
    refuse "$key2" "$said" "nginx_look, with a world-readable file in /etc/nginx/minerbot, opened to anyone"
    chmod 700 /etc/nginx/minerbot
    rm -rf /etc/nginx/conf.d/zz-look.conf "/etc/nginx/conf.d/includes-optional/cp extra.conf" \
        "/etc/nginx/conf.d/includes-optional/open extra.conf" /srv/outside /etc/nginx/spelled /srv/private-only \
        /etc/nginx/conf.d/includes-optional/linked.conf
}

# A put-back cut off as it renames the old key back, after the test with every site refused a change with a new
# certificate: our conf.d file stays out until every other file is back, so nginx -t passes, and the undo leaves no copy
# of the old key behind
put_back_cut_off() {
    local before out answer
    put_in_place /work/replaced
    # Another file that clashes with a line only the new set's conf.d file has: the change passes on its own and fails
    # with every site, once its files are in place
    # shellcheck disable=SC2016 # nginx's variable
    printf 'log_format mbtest_clash '\''$remote_addr'\'';\n' | put_file 644 /etc/nginx/conf.d/zz-clash.conf
    out=$(nginx_test) || problem "nginx -t refused the files this check starts from" "$out"
    reload_nginx
    before=$(snapshot)
    cut_at /etc/nginx/minerbot/origin-key.pem restore
    run_common nginx_change /work/clash2
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "the put-back wasn't cut off (exit code $status)" "$said"
    expect 'duplicate "log_format" name "mbtest_clash"' "$said" "nginx's own message, before the put-back"
    [ "$reloaded" = 0 ] || problem "the cut-off change ran cPanel's reload $reloaded time(s)"
    [ -e "$marker" ] || problem "the cut-off put-back left no marker"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] \
        || problem "our conf.d file was back before the old key"
    out=$(nginx_test) || problem "nginx -t fails after a put-back was cut off" "$out"
    run_common nginx_undo_cut_off
    outcome undone 1
    same_as "$before"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key from before the change, at the stand-in dashboard"
    rm -f /etc/nginx/conf.d/zz-clash.conf
    reload_nginx
}

# cPanel's reload loads the new files and then reports failure. The old files go back, and nginx loads them again, so
# it runs what the disk and dashboard.env hold.
reload_fails_after_loading() {
    local before answer
    put_in_place /work/replaced
    before=$(snapshot)
    cp -p /scripts/restartsrv_nginx /work/restartsrv_nginx.real
    rm -f /work/reload-failed
    put_file 755 /scripts/restartsrv_nginx << 'EOF'
#!/bin/sh
echo "$*" >> /work/reloads
nginx -s reload || exit 1
if [ ! -e /work/reload-failed ]; then
    : > /work/reload-failed
    echo "Waiting for nginx to restart gracefully ...failed (made up, after the reload)." >&2
    exit 1
fi
EOF
    run_common nginx_change /work/clash
    cp -p /work/restartsrv_nginx.real /scripts/restartsrv_nginx
    outcome refused 2
    expect "nginx was reloaded with them" "$said" "the message"
    same_as "$before"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    [ "$(served_print)" = "$(file_print /work/replaced/origin-cert.pem)" ] \
        || problem "nginx serves another certificate than the one on disk" "$(served_print)"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key on disk, at the stand-in dashboard"
}

# Every reload reports failure after loading: the marker stays, so the next setup run's undo loads the old files
reload_fails_twice() {
    local before answer
    put_in_place /work/replaced
    before=$(snapshot)
    cp -p /scripts/restartsrv_nginx /work/restartsrv_nginx.real
    put_file 755 /scripts/restartsrv_nginx << 'EOF'
#!/bin/sh
echo "$*" >> /work/reloads
nginx -s reload || exit 1
echo "Waiting for nginx to restart gracefully ...failed (made up, after the reload)." >&2
exit 1
EOF
    run_common nginx_change /work/clash
    cp -p /work/restartsrv_nginx.real /scripts/restartsrv_nginx
    outcome refused 2
    expect "cPanel's reload failed again, with the old files back" "$said" "the message"
    [ -e "$marker" ] || problem "the marker is gone, though the reload of the old files failed"
    run_common nginx_undo_cut_off
    outcome undone 1
    same_as "$before"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there after the undo"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key on disk, at the stand-in dashboard"
}

# A first install cut off between two renames: conf.d must not hold our file until every file it names is in place
first_install_cut_off() {
    local before out cut name
    for cut in /etc/nginx/minerbot/origin-key.pem /etc/nginx/conf.d/minerbot-dashboard.conf; do
        nothing_in_place
        before=$(snapshot)
        cut_at "$cut"
        run_common nginx_change /work/first
        rm -f /usr/local/bin/mv
        [ "$status" = 137 ] || problem "cut at $cut: nginx_change wasn't cut off (exit code $status)" "$said"
        [ "$reloaded" = 0 ] || problem "cut at $cut: the cut-off change ran cPanel's reload"
        [ -e "$marker" ] || problem "cut at $cut: no marker"
        [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] \
            || problem "cut at $cut: our conf.d file was in place before every file it names"
        if [ "$cut" = /etc/nginx/conf.d/minerbot-dashboard.conf ]; then
            for name in origin-cert.pem origin-key.pem proxy-key.conf; do
                [ "$(sha "/etc/nginx/minerbot/$name")" = "$(sha "/work/first/$name")" ] \
                    || problem "cut at $cut: the new $name wasn't in place before the conf.d file's rename"
            done
        fi
        out=$(nginx_test) || problem "cut at $cut: nginx -t fails after a first install was cut off" "$out"
        run_common nginx_undo_cut_off
        outcome undone 1
        same_as "$before"
        [ ! -e "$marker" ] || problem "cut at $cut: the marker is still there"
    done
}

# --remove-nginx after a first install that was cut off: the undo takes the new files away, and the marker goes
cut_off_first_install_removed() {
    local out
    nothing_in_place
    cut_at /etc/nginx/minerbot/origin-key.pem
    run_common nginx_change /work/first
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "nginx_change wasn't cut off (exit code $status)" "$said"
    [ -e "$marker" ] || problem "the cut-off change left no marker"
    [ -e /etc/nginx/minerbot/origin-cert.pem ] || problem "the new certificate isn't in place after the cut"
    run_common nginx_remove
    outcome absent 0
    expect "An nginx change was cut off earlier. Its files are back as they were before it" "$said" "the message"
    # The undo changed files, so the message names it and never says that nothing was changed
    expect "MinerBot's file isn't in nginx once the cut-off change is undone (above), so nothing else was changed." "$said" \
        "the message after the undo"
    refuse "Nothing was changed." "$said" "the message after the undo"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] || problem "our conf.d file is there"
    [ -z "$(find /etc/nginx/minerbot -mindepth 1 2>&1)" ] \
        || problem "/etc/nginx/minerbot still holds files of the cut-off install" "$(ls -lA /etc/nginx/minerbot 2>&1)"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    out=$(nginx_test) || problem "nginx -t fails" "$out"
}

# TERM at the key's rename, as systemd sends it to the whole unit, doesn't cut the change: INT, TERM and HUP are ignored
# from the first change until the marker is gone
term_during_change() {
    local answer
    put_in_place /work/replaced
    : > /work/nginx-calls
    put_file 755 /usr/local/bin/mv << 'EOF'
#!/bin/sh
for last; do :; done
if [ "$last" = /etc/nginx/minerbot/origin-key.pem ]; then
    kill -TERM "$PPID"
fi
exec /usr/bin/mv "$@"
EOF
    run_common nginx_change /work/clash
    rm -f /usr/local/bin/mv
    outcome reloaded 1
    calls_were "the test on its own, then the test with every site, then one reload" "-t -q -c $standalone" "-t -q" \
        "-s reload"
    installed_as /work/clash
    [ "$(served_print)" = "$(file_print /work/clash/origin-cert.pem)" ] \
        || problem "nginx doesn't serve the new certificate" "$(served_print)"
    answer=$(as_cloudflare /admin)
    expect "key=$key3 " "$answer" "the new proxy key, at the stand-in dashboard"
}

# A marker whose backup was cut off mid-copy, before its "complete" file: no nginx file had changed yet, so nothing is
# put back, nginx isn't tested or reloaded, the marker goes, and the result is none, so no later message claims an undo
cut_off_backup_ignored() {
    local before dir=/opt/minerbot/state/nginx-backup/20261106T000000Z
    put_in_place /work/replaced
    before=$(snapshot)
    install -d -m 700 "$dir"
    : > "$dir/missing"
    cp -p /etc/nginx/conf.d/minerbot-dashboard.conf "$dir/"
    head -c 200 /etc/nginx/minerbot/origin-cert.pem > "$dir/origin-cert.pem"
    printf '%s\n' "$dir" > "$marker"
    : > /work/nginx-calls
    run_common nginx_undo_cut_off
    outcome none 0
    expect "so there was nothing to undo" "$said" "the message"
    same_as "$before"
    calls_were "none"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    # --remove-nginx's first step
    printf '%s\n' "$dir" > "$marker"
    run_common nginx_undo_cut_off --no-reload
    outcome none 0
    same_as "$before"
    # Cut off before the backup's folder was made
    rm -rf "$dir"
    printf '%s\n' "$dir" > "$marker"
    : > /work/nginx-calls
    run_common nginx_undo_cut_off
    outcome none 0
    calls_were "none, for a marker whose folder was never made"
    same_as "$before"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
}

# The undo of a cut-off change while another conf.d file is broken: the files go back, nginx -t fails, nothing is
# reloaded, and the result is failed, which stops setup before it changes anything else
undo_fails_with_other_broken_file() {
    local before out
    put_in_place /work/replaced
    before=$(snapshot)
    cut_at /etc/nginx/minerbot/origin-key.pem
    run_common nginx_change /work/clash
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "nginx_change wasn't cut off (exit code $status)" "$said"
    [ -e "$marker" ] || problem "the cut-off change left no marker"
    printf 'this_is_not_a_directive on;\n' | put_file 644 /etc/nginx/conf.d/zz-broken.conf
    : > /work/nginx-calls
    run_common nginx_undo_cut_off
    outcome failed 0
    expect "nginx -t fails with them (above), so nginx wasn't reloaded" "$said" "the message"
    calls_were "only the test with every site" "-t -q"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    rm -f /etc/nginx/conf.d/zz-broken.conf
    same_as "$before"
    out=$(nginx_test) || problem "nginx -t fails once the broken file is gone" "$out"
}

# --remove-nginx after a cut-off change: the change is undone first, so the old certificate and key are back together,
# and the next setup run, which keeps the certificate nginx has, works
removed_after_cut_off() {
    local before copy out
    put_in_place /work/replaced
    before=$(folder_state /etc/nginx/minerbot)
    cut_at /etc/nginx/minerbot/origin-key.pem
    run_common nginx_change /work/clash
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "nginx_change wasn't cut off (exit code $status)" "$said"
    [ -e "$marker" ] || problem "the cut-off change left no marker"
    run_common nginx_remove
    outcome removed 1
    expect "Its files are back as they were before it" "$said" "the message"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] || problem "our file is still in conf.d"
    out=$(nginx_test) || problem "nginx -t fails after the removal" "$out"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    [ ! -e /etc/nginx/minerbot/.staging ] || problem "the staging folder is still there"
    [ "$(folder_state /etc/nginx/minerbot)" = "$before" ] \
        || problem "/etc/nginx/minerbot isn't as it was before the cut-off change" \
            "$(diff <(printf '%s\n' "$before") <(folder_state /etc/nginx/minerbot))"
    copy=$(find /opt/minerbot/state/nginx-backup -mindepth 1 -maxdepth 1 -type d | LC_ALL=C sort | tail -n 1)
    [ "$(sha "$copy/minerbot-dashboard.conf")" = "$(sha /work/replaced/minerbot-dashboard.conf)" ] \
        || problem "the newest backup doesn't hold our file from before the cut-off change"
    # README.txt's next step: setup again, keeping the certificate and key nginx has
    make_set /work/kept /etc/nginx/minerbot "$key2"
    run_common nginx_change /work/kept
    outcome reloaded 1
}

# An earlier conf.d file names the dashboard's host with another certificate and also answers 403. nginx keeps the
# first block for a name with only a warning, so nginx -t and the reload pass: only the certificate shows it.
another_block_answers() {
    local answer
    put_in_place /work/second
    put_file 644 /etc/nginx/conf.d/aa-other-bot.conf << EOF
server {
    listen 443 ssl;
    listen [::]:443 ssl;
    server_name $host;
    ssl_certificate /etc/nginx/other-sites/origin-cert.pem;
    ssl_certificate_key /etc/nginx/other-sites/origin-key.pem;
    return 403;
}
EOF
    run_common nginx_change /work/replaced
    outcome problem 1
    expect "the certificate it serves for $host isn't the installed one" "$said" "the smoke check's message"
    refuse "nginx serves the installed certificate" "$said" "the smoke check's message"
    refuse "where 403 was expected" "$said" "the smoke check's message, for a 403"
    answer=$(request "https://$host/")
    expect "HTTP/1.1 403" "$answer" "a request from this server, which the other block answers"
    rm -f /etc/nginx/conf.d/aa-other-bot.conf
    reload_nginx
}

# cPanel's reload exits 0 without signalling nginx, which keeps its workers and the old files: no new worker, so the old
# files go back, and the marker stays, since nginx took no reload of them either; the undo then loads them
reload_not_taken() {
    local before answer
    put_in_place /work/second
    before=$(snapshot)
    cp -p /scripts/restartsrv_nginx /work/restartsrv_nginx.real
    printf '#!/bin/sh\nexit 0\n' | put_file 755 /scripts/restartsrv_nginx
    run_common nginx_change /work/replaced
    cp -p /work/restartsrv_nginx.real /scripts/restartsrv_nginx
    outcome refused 0
    expect "cPanel's reload exited 0, but nginx kept its old worker processes for" "$said" "the message"
    expect "nginx didn't take cPanel's reload (above), so it still runs the old files, and they're back." "$said" "the message"
    expect "WARNING: nginx didn't take the reload of the old files either" "$said" "the message"
    refuse "nginx reloaded with the new files" "$said" "the message"
    same_as "$before"
    [ -e "$marker" ] || problem "the marker is gone, though nginx took no reload of the old files either"
    [ "$(served_print)" = "$(file_print /work/certs/a/origin-cert.pem)" ] \
        || problem "nginx serves another certificate than the one on disk" "$(served_print)"
    run_common nginx_undo_cut_off
    outcome undone 1
    [ ! -e "$marker" ] || problem "the marker is still there after the undo"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key on disk, at the stand-in dashboard"
}

# A change of the proxy key alone, which neither the 403 check nor the certificate can see, while a file another account
# changed listens on an address another program holds: nginx -t passes, since it never binds, but nginx's master can't
# apply the new files at the reload. It keeps its workers and its old files, and says why only in its error log, while
# cPanel's reload exits 0. The old files go back, nginx still sends the old key, and the marker stays until a reload
# nginx takes, which the undo makes once the address is free.
reload_refused_by_master() {
    local before answer holder out
    put_in_place /work/second
    timeout -k 5 120 openssl s_server -quiet -accept 127.0.0.1:8099 -cert /work/certs/a/origin-cert.pem \
        -key /work/certs/a/origin-key.pem < /dev/null > /dev/null 2>&1 &
    holder=$!
    for _ in $(seq 50); do
        if (exec 3<> /dev/tcp/127.0.0.1/8099) 2> /dev/null; then break; fi
        sleep 0.1
    done
    printf 'server {\n    listen 127.0.0.1:8099;\n    server_name holder.example;\n    return 404;\n}\n' \
        | put_file 644 /etc/nginx/conf.d/zz-holder.conf
    out=$(nginx_test) || problem "nginx -t refused the block on the held address, so this check proves nothing" "$out"
    before=$(snapshot)
    : > /var/log/nginx/error.log
    unsettled=yes run_common nginx_change /work/rekeyed
    outcome refused 2
    expect "cPanel's reload exited 0, but nginx kept its old worker processes for" "$said" "the message"
    expect "nginx didn't take cPanel's reload (above), so it still runs the old files, and they're back." "$said" "the message"
    expect "WARNING: nginx didn't take the reload of the old files either" "$said" "the message"
    refuse "nginx reloaded with the new files" "$said" "the message"
    same_as "$before"
    [ -e "$marker" ] || problem "the marker is gone, though nginx took no reload of the old files"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key nginx still sends, at the stand-in dashboard"
    expect "bind() to 127.0.0.1:8099 failed" "$(cat /var/log/nginx/error.log 2>&1)" "nginx's own reason, in its error log"
    kill "$holder" 2> /dev/null || true
    wait "$holder" 2> /dev/null || true
    rm -f /etc/nginx/conf.d/zz-holder.conf
    run_common nginx_undo_cut_off
    outcome undone 1
    [ ! -e "$marker" ] || problem "the marker is still there after the undo"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key on disk, at the stand-in dashboard"
}

# WHM's piped logging goes off while MinerBot's file is in nginx: cPanel deletes the pipelog module's load line, runs the
# programs in its config-scripts folder, then tests and reloads nginx. A "pipelog off;" line, as while it was on, fails
# nginx -t for every site; the hook nginx_change installed empties pipelog.conf, so the test passes and nginx reloads.
# Then on again: the hook writes the line back. This nginx has no pipelog module, so the module's file holds only a
# comment here, and the hook reads it by its name alone.
pipelog_follows_cpanel() {
    local out answer module=/etc/nginx/conf.d/modules/ngx_http_pipelog_module.conf
    put_in_place /work/second
    run_common nginx_change /work/replaced
    outcome reloaded 1
    installed_as /work/replaced
    printf 'pipelog off;\n' > /etc/nginx/minerbot/pipelog.conf
    if out=$(nginx_test); then
        problem "nginx -t passed with a pipelog line and no pipelog module, so this check proves nothing"
    else
        expect 'unknown directive "pipelog" in /etc/nginx/minerbot/pipelog.conf' "$out" "nginx's message"
    fi
    out=$(timeout -k 5 30 "$hook" 2>&1) || problem "the hook failed" "$out"
    [ -f /etc/nginx/minerbot/pipelog.conf ] && [ ! -s /etc/nginx/minerbot/pipelog.conf ] \
        || problem "the hook didn't empty pipelog.conf without the module's file" "$(cat /etc/nginx/minerbot/pipelog.conf 2>&1)"
    out=$(nginx_test) || problem "nginx -t fails after the hook" "$out"
    reload_nginx
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "Cloudflare's request once piped logging is off"
    printf '# made up: this image has no pipelog module\n' | put_file 644 "$module"
    out=$(timeout -k 5 30 "$hook" 2>&1) || problem "the hook failed with the module's file there" "$out"
    [ "$(cat /etc/nginx/minerbot/pipelog.conf 2>&1)" = "pipelog off;" ] \
        || problem "the hook didn't write \"pipelog off;\" with the module's file there" "$(cat /etc/nginx/minerbot/pipelog.conf 2>&1)"
    rm -f "$module"
    out=$(timeout -k 5 30 "$hook" 2>&1) || problem "the hook failed once the module's file was gone again" "$out"
    out=$(nginx_test) || problem "nginx -t fails at the end of the check" "$out"
}

# An undo whose backup is older than WHM's piped logging setting: the cut-off change began while cPanel loaded its
# pipelog module, so the backup's pipelog.conf says "pipelog off;", and piped logging went off before the undo. The undo
# writes pipelog.conf for cPanel's setting before our conf.d file comes back, so one cut off in between never leaves
# nginx a live "pipelog off;" without the module, which fails nginx -t for every site. This nginx has no pipelog module,
# so it refuses the line whatever the module's file holds, and that file holds only a comment here.
pipelog_undo_cut_off() {
    local before out answer module=/etc/nginx/conf.d/modules/ngx_http_pipelog_module.conf
    put_in_place /work/second
    # The files as the undo must leave them, pipelog.conf as cPanel's setting will need it by then
    printf '' | put_file 600 /etc/nginx/minerbot/pipelog.conf
    before=$(snapshot)
    # Piped logging is on as the change begins, so its backup keeps "pipelog off;". It's cut off at the new key's rename.
    printf '# made up: this image has no pipelog module\n' | put_file 644 "$module"
    printf 'pipelog off;\n' | put_file 600 /etc/nginx/minerbot/pipelog.conf
    cut_at /etc/nginx/minerbot/origin-key.pem
    run_common nginx_change /work/replaced
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "nginx_change wasn't cut off (exit code $status)" "$said"
    # Then piped logging goes off: cPanel deletes the module's file, and the hook empties pipelog.conf
    rm -f "$module"
    printf '' | put_file 600 /etc/nginx/minerbot/pipelog.conf
    out=$(nginx_test) || problem "nginx -t fails before the undo" "$out"
    # The undo, cut off as it writes pipelog.conf for cPanel's setting over the backup's "pipelog off;"
    cut_at /etc/nginx/minerbot/pipelog.conf pipelog
    run_common nginx_undo_cut_off
    rm -f /usr/local/bin/mv
    [ "$status" = 137 ] || problem "the undo wasn't cut off as it wrote pipelog.conf (exit code $status)" "$said"
    [ ! -e /etc/nginx/conf.d/minerbot-dashboard.conf ] \
        || problem "our conf.d file was back while pipelog.conf still said \"pipelog off;\" without the module"
    out=$(nginx_test) || problem "nginx -t fails after the undo was cut off" "$out"
    # A crash or a power loss is followed by a boot: nginx must start, and the other sites answer
    restart_nginx
    answer=$(request https://other.example/)
    expect "account site" "$answer" "the account's own site, after nginx started again"
    run_common nginx_undo_cut_off
    outcome undone 1
    same_as "$before"
    [ ! -e "$marker" ] || problem "the marker state/nginx-change is still there"
    answer=$(as_cloudflare /admin)
    expect "key=$key2 " "$answer" "the proxy key from before the change, at the stand-in dashboard"
    nginx_running
}

# ---------- In the container: the layout, then the checks

inside_container() {
    local out
    trap 'stop "The container'\''s setup failed at line $LINENO."' ERR
    install -d -m 755 /etc/nginx/conf.d/modules /etc/nginx/conf.d/users /etc/nginx/conf.d/includes-optional \
        /etc/nginx/other-sites /scripts /etc/nginx/ea-nginx /etc/nginx/ea-nginx/config-scripts \
        /etc/nginx/ea-nginx/config-scripts/global
    install -d -m 700 /work/certs
    # procps isn't in this image, and the server has it. A stand-in for the one form of pgrep common.sh uses, pgrep -P
    # <PID> -f <pattern>: the processes whose parent is that PID and whose command line matches, from /proc
    if ! command -v pgrep > /dev/null; then
        put_file 755 /usr/local/bin/pgrep << 'EOF'
#!/bin/bash
if [ $# != 4 ] || [ "$1" != -P ] || ! [[ $2 =~ ^[0-9]+$ ]] || [ "$3" != -f ]; then
    echo "This stand-in only knows pgrep -P <PID> -f <pattern>." >&2
    exit 2
fi
found=1
for dir in /proc/[0-9]*; do
    grep -qx "PPid:[[:space:]]*$2" "$dir/status" 2> /dev/null || continue
    cmd=$(tr '\0' ' ' < "$dir/cmdline" 2> /dev/null) || continue
    if [[ $cmd =~ $4 ]]; then
        echo "${dir#/proc/}"
        found=0
    fi
done
exit "$found"
EOF
    fi

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
    # The dashboard's socket folder as on the server: only the dashboard's user (1654) may write it, and nginx's workers
    # (nobody) may enter it. The set-group-ID bit gives the stand-in's socket that group.
    install -d -m 2750 -o 1654 -g nobody /run/minerbot-dashboard

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
    # The dashboard, played by nginx on the dashboard's socket: it answers with what it was sent
    put_file 644 /etc/nginx/conf.d/fake-dashboard.conf << 'EOF'
server {
    listen unix:/run/minerbot-dashboard/dashboard.sock;
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
    # second's certificate with another key: a change neither the 403 check nor the certificate can see
    make_set /work/rekeyed /work/certs/a "$key3"
    # clash's files, with a line in the conf.d file that clashes with another file the check adds
    make_set /work/clash2 /work/certs/c "$key3"
    # shellcheck disable=SC2016 # nginx's variable
    printf 'log_format mbtest_clash '\''$remote_addr'\'';\n' >> /work/clash2/minerbot-dashboard.conf

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
    verdict "1. A first install: the test on its own with nginx.conf's main context and an absolute -c, then the test with every site, then one reload. A request from this server gets 403, and Cloudflare's reaches the dashboard's socket with the key. The modes are 700, 600 and 644."
    main_context_copied || true
    verdict "2. With nginx's compiled-in user gone, the test on its own still passes, because nginx.conf's user line comes first (R6)."
    refused_on_its_own || true
    verdict "3. A file nginx refuses on its own changes nothing: conf.d and /etc/nginx/minerbot are the same by SHA-256, the staging folder, the marker and the run's backup folder are gone, and nginx wasn't reloaded."
    refused_with_every_site || true
    verdict "4. A file that passes on its own but clashes with another file (R7): every file back byte for byte, the ones that weren't there gone again, nginx -t passes, no reload, and the other sites answer."
    certificate_replaced || true
    verdict "5. A new certificate arrives by rename, with one reload, and nginx serves it for the name."
    removed_while_test_fails || true
    verdict "6. --remove-nginx while nginx -t fails for our missing certificate: our file goes, with a copy kept, nginx -t passes, one reload. Then without /etc/nginx/minerbot, nginx -t passes, nginx reloads and the other sites answer."
    remove_finds_other_file || true
    verdict "7. --remove-nginx while another file is broken: our file is back byte for byte, nginx wasn't reloaded, and the message says the problem is in another file."
    cut_off_undone || true
    verdict "8. A change cut off between two renames (the new certificate beside the old key) leaves our conf.d file out, so nginx -t passes and nginx starts again as after a reboot; the undo puts every file back byte for byte, with one reload, and the marker goes."
    look_keeps_secrets || true
    verdict "9. nginx_look prints the http-level lines, under each file's whole path, but not what server or types blocks hold, nor the proxy key, nor any root-only include's secrets, which nginx -T itself prints, whether its path has a space, lies outside /etc/nginx or has a \"..\", nor a world-readable include in a folder only root can enter, even through a link, nor anything in /etc/nginx/minerbot, even a world-readable file there with the folder open to anyone."
    put_back_cut_off || true
    verdict "10. A put-back cut off as it renames the old key back leaves our conf.d file out, so nginx -t passes; the undo puts every file back byte for byte, with no copy of the old key left beside it."
    reload_fails_after_loading || true
    verdict "11. cPanel's reload loads the new files, then fails: the old files go back and nginx is reloaded with them, so it sends the key on disk and serves the certificate on disk, and the marker goes."
    reload_fails_twice || true
    verdict "12. cPanel's reload fails again with the old files back: the marker stays, and the undo then puts the files back and reloads, so nginx runs the files on disk."
    first_install_cut_off || true
    verdict "13. A first install cut off as the key, then as the conf.d file, is renamed in: our conf.d file is out until every file it names is in place, nginx -t passes, and the undo takes the new files away again with one reload."
    cut_off_first_install_removed || true
    verdict "14. --remove-nginx after a first install that was cut off: the new files go, the result is absent with no reload, the message names the undo, and the marker is gone."
    term_during_change || true
    verdict "15. TERM at the key's rename doesn't cut the change: the new files, one test with every site, one reload, no marker."
    cut_off_backup_ignored || true
    verdict "16. A marker whose backup was cut off mid-copy (no complete file), or never made, changes nothing: no file put back, no nginx -t, no reload, and the marker is gone."
    undo_fails_with_other_broken_file || true
    verdict "17. The undo of a cut-off change while another file is broken puts the files back, runs nginx -t once, doesn't reload, and says failed."
    removed_after_cut_off || true
    verdict "18. --remove-nginx after a cut-off change undoes it first: /etc/nginx/minerbot is as before it, byte for byte, with no staging folder and no marker, and setup with the certificate kept then works."
    MINERBOT_TEST_FAST=1
    export MINERBOT_TEST_FAST
    another_block_answers || true
    verdict "19. An earlier block for the name with another certificate that also answers 403: the smoke check says problem, for the certificate alone."
    reload_not_taken || true
    verdict "20. A reload that exits 0 without signalling nginx: no new worker, so the old files go back byte for byte, nginx still serves the old certificate, the marker stays, and the undo then loads the files on disk."
    unset MINERBOT_TEST_FAST
    reload_refused_by_master || true
    verdict "21. A proxy key alone changes while another block listens on an address another program holds: nginx -t passes, nginx's master refuses the reload and keeps its workers while cPanel's reload exits 0, so the old files go back byte for byte, nginx still sends the old key, its error log says why, the marker stays, and once the address is free the undo loads the files on disk."
    pipelog_follows_cpanel || true
    verdict "22. nginx_change installs cPanel's hook and an empty pipelog.conf, as this nginx has no pipelog module; a \"pipelog off;\" line without the module fails nginx -t, the hook empties it, and nginx -t passes and nginx reloads; with the module's file there, the hook writes the line again."
    pipelog_undo_cut_off || true
    verdict "23. An undo whose backup says \"pipelog off;\", from before WHM's piped logging went off, cut off as it writes pipelog.conf for cPanel's setting: our conf.d file is still out, so nginx -t passes and nginx starts again as after a reboot; the next undo puts every file back byte for byte, pipelog.conf as cPanel's setting needs, with one reload."

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
    # Its main process is sleep 420, so even a run killed before it can clean up leaves nothing running after 7
    # minutes. Docker's init runs it, and reaps nginx's master once it stops, as a server's init does: sleep wouldn't,
    # and the stopped master would stay in /proc as a zombie. IPv6 is on, as the server's blocks listen on [::] too.
    out=$(timeout -k 10 120 docker run -d --rm --init --name "$name" --network none \
        --sysctl net.ipv6.conf.all.disable_ipv6=0 "$image" sleep 420 2>&1) \
        || stop "Couldn't start the container $name: $out"
    copy_in "$0" /work/nginx-procedure-test.sh
    copy_in "$deploy/common.sh" /opt/minerbot/common.sh
    copy_in "$deploy/minerbot-dashboard.conf" /opt/minerbot/minerbot-dashboard.conf
    echo "The real common.sh and minerbot-dashboard.conf are in /opt/minerbot in $name."

    # The checks print their own lines and count. This counts the ok lines as well, for when the limit stops them.
    while IFS= read -r -t 420 line; do
        case $line in
            "mbtest exit code "*) status=${line#mbtest exit code } ;;
            *)
                printf '%s\n' "$line"
                if [[ $line == "ok: "* ]]; then passed=$((passed + 1)); fi
                ;;
        esac
    done < <(
        code=0
        timeout -k 10 390 docker exec "$name" bash /work/nginx-procedure-test.sh --inside 2>&1 || code=$?
        echo "mbtest exit code $code"
    )
    case $status in
        0) exit 0 ;;
        1) exit 1 ;;
        124 | 137) stop "The checks didn't finish within 390 seconds." ;;
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

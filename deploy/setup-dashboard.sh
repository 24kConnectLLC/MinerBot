#!/usr/bin/env bash
# Sets up MinerBot's web dashboard, and changes its settings later. The owner runs it as root, in WHM's Terminal or over
# SSH. It asks only for what only the owner has: the Cloudflare Origin certificate and its private key, the MinerBot
# Dashboard application's client ID and secret, the owners' and admins' Discord user IDs, and Cloudflare Access's team
# domain and AUD tag when Access is on. It makes the proxy key and the dashboard's database password itself and never
# shows them. The answers are checked here; the changes run as a systemd unit of their own, minerbot-setup, which this
# terminal follows. They change only what differs, so running this again finishes whatever an interruption left.
# Usage:
#   ./setup-dashboard.sh                         asks every question; Enter keeps a value, and keep keeps a paste
#   ./setup-dashboard.sh --no-questions          keeps every current value, installs a changed nginx file, applies the
#                                                rights again and repairs whatever differs, then asks "Go ahead?"
#   ./setup-dashboard.sh --no-questions --yes    the same without that question, for a run without a terminal
#   --rotate-proxy-key, --rotate-db-password     make a new proxy key or database password; either goes with the above
#   ./setup-dashboard.sh --remove-nginx [--yes]  the emergency path: takes MinerBot's file out of nginx, nothing else
# nginx changes only after the new files pass nginx -t on their own and then with every site, and a failure puts every
# file back byte for byte. Exit code: 0 done; 1 refused or stopped, with the reason.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

action=setup
subject=dashboard
step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: ./setup-dashboard.sh [--no-questions] [--yes] [--rotate-proxy-key] [--rotate-db-password], or ./setup-dashboard.sh --remove-nginx [--yes]"
env_file=$home/dashboard.env
env_header_1="# The web dashboard's settings. setup-dashboard.sh writes this file: run it again to change them."
env_header_2="# It keeps lines under other names. The dashboard reads the file only when its container is created."
managed=(Dashboard__PublicOrigin Dashboard__ProxyKey Dashboard__Owners Dashboard__Admins Discord__ClientId
    Discord__ClientSecret CloudflareAccess__TeamDomain CloudflareAccess__Audience ConnectionStrings__DefaultConnection
    ConnectionStrings__BotConnection)

mode=questions
yes=no
rotate_key=no
rotate_pw=no
apply_dir=
options=0
while [ $# -gt 0 ]; do
    case $1 in
        --no-questions | --remove-nginx)
            [ "$mode" = questions ] || die "$usage"
            mode=${1#--}
            ;;
        --yes) yes=yes ;;
        --rotate-proxy-key) rotate_key=yes ;;
        --rotate-db-password) rotate_pw=yes ;;
        --apply)
            [ $# -ge 2 ] || die "$usage"
            apply_dir=$2
            shift
            options=$((options - 1))
            ;;
        *) die "$usage" ;;
    esac
    options=$((options + 1))
    shift
done
if [ "$mode" = remove-nginx ] && { [ "$rotate_key" = yes ] || [ "$rotate_pw" = yes ]; }; then
    die "--remove-nginx goes only with --yes. $usage"
fi
if [ -n "$apply_dir" ] && [ "$options" != 0 ]; then
    die "--apply goes alone: it's only for the run this script starts itself."
fi

# ---- Checks of the answers. Each prints why it refuses, and may tidy the answer in $answer.

check_client_id() {
    answer=$1
    if [[ $answer =~ ^[0-9]{17,20}$ ]]; then
        return 0
    fi
    echo "The client ID is 17 to 20 digits, on the MinerBot Dashboard application's OAuth2 page."
    return 1
}

check_secret() {
    answer=$1
    if ! env_safe "$answer"; then
        echo "That can't go into an env file: $env_rule. A Discord client secret has only letters, digits, - and _: copy it again."
        return 1
    fi
    if [[ $answer =~ ^[A-Za-z0-9_-]{16,128}$ ]]; then
        return 0
    fi
    echo "That isn't a Discord client secret, which is 16 to 128 letters, digits, - and _. Copy it again from the OAuth2 page."
    return 1
}

tidy_ids() {   # tidy_ids <list>: the list without spaces, in $answer; false when an item isn't a Discord user ID
    local id
    local -a ids=()
    answer=${1// /}
    IFS=, read -r -t 5 -a ids <<< "$answer" || true
    [ ${#ids[@]} -gt 0 ] || return 1
    for id in "${ids[@]}"; do
        [[ $id =~ ^[0-9]{17,20}$ ]] || return 1
    done
    answer=$(IFS=,; echo "${ids[*]}")
}

check_owners() {
    if tidy_ids "$1"; then
        return 0
    fi
    echo "Give one or more Discord user IDs, separated by commas. Each is 17 to 20 digits: Developer Mode on, then right-click a name and Copy User ID."
    return 1
}

check_admins() {
    local id
    local -a ids=()
    answer=${1// /}
    if [ -z "$answer" ] || [ "${answer,,}" = none ]; then
        answer=
        return 0
    fi
    if ! tidy_ids "$answer"; then
        echo "Give one or more Discord user IDs, separated by commas, or none. Each is 17 to 20 digits."
        return 1
    fi
    IFS=, read -r -t 5 -a ids <<< "$answer" || true
    for id in "${ids[@]}"; do
        if [[ ",$owners," == *",$id,"* ]]; then
            echo "$id is on both lists. Put each person on one list only."
            return 1
        fi
    done
}

check_team() {
    answer=${1,,}
    if [ -z "$answer" ] || [ "$answer" = none ]; then
        answer=
        return 0
    fi
    answer=${answer#https://}
    answer=${answer#http://}
    answer=${answer%%/*}
    if [[ $answer != *.* ]]; then
        answer=$answer.cloudflareaccess.com
    fi
    if ! [[ $answer =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.cloudflareaccess\.com$ ]]; then
        echo "Give the team's name, such as yourteam, or its domain, yourteam.cloudflareaccess.com, or none to leave Access off."
        return 1
    fi
    # A new team domain must answer with its keys, which catches a typo
    if [ "$answer" != "${cur_team:-}" ] && ! access_keys_ok "$answer"; then
        echo "Couldn't get Cloudflare Access's keys from https://$answer/cdn-cgi/access/certs. Check the team domain in Zero Trust's settings."
        return 1
    fi
}

access_keys_ok() {   # access_keys_ok <team domain>: Cloudflare Access answers there with at least one RSA key
    run_for 20 curl -fsS --max-time 15 --proto =https --max-redirs 0 "https://$1/cdn-cgi/access/certs" 2> /dev/null \
        | jq -e '[.keys[] | select(.kty == "RSA")] | length > 0' > /dev/null 2>&1
}

check_aud() {
    answer=${1,,}
    if [[ $answer =~ ^[0-9a-f]{64}$ ]]; then
        return 0
    fi
    echo "The AUD tag is 64 characters, 0-9 and a-f, on the Access application's Overview page."
    return 1
}

# The checks a pasted certificate passes. -checkhost alone would accept a wildcard or a certificate with more names, so the
# subjectAltName must be exactly this one name.
certificate_ok() {   # certificate_ok <file>
    local names subject issuer ends
    if ! run_for 10 openssl x509 -noout -in "$1" > /dev/null 2>&1; then
        echo "openssl can't parse that as a certificate. Copy the Origin certificate from Cloudflare again."
        return 1
    fi
    names=$(run_for 10 openssl x509 -noout -ext subjectAltName -in "$1" 2> /dev/null | sed -n '2,$p' | tr -d ' \n') || names=
    if [ "$names" != "DNS:$public_host" ]; then
        echo "That certificate must name $public_host and nothing else, and it names: ${names:-nothing}. Make one for that name only in Cloudflare: SSL/TLS, Origin Server."
        return 1
    fi
    if ! run_for 10 openssl x509 -noout -checkend 2592000 -in "$1" > /dev/null 2>&1; then
        echo "That certificate ends within 30 days. Make a new one in Cloudflare: SSL/TLS, Origin Server."
        return 1
    fi
    subject=$(run_for 10 openssl x509 -noout -subject -in "$1" 2> /dev/null) || subject=
    issuer=$(run_for 10 openssl x509 -noout -issuer -in "$1" 2> /dev/null) || issuer=
    ends=$(cert_end "$1") || ends="(unknown)"
    echo "The certificate names $public_host only."
    echo "  ${subject/#subject=/Subject: }"
    echo "  ${issuer/#issuer=/Issuer: }"
    echo "  It ends $ends."
}

key_ok() {   # key_ok <key file> <certificate file>: a key without a passphrase that belongs to the certificate
    if ! run_for 10 openssl pkey -noout -passin pass: -in "$1" > /dev/null 2>&1; then
        echo "openssl can't parse that as a private key without a passphrase. Copy the key Cloudflare showed with the certificate again."
        return 1
    fi
    if ! cmp -s <(run_for 10 openssl x509 -noout -pubkey -in "$2" 2> /dev/null) \
        <(run_for 10 openssl pkey -pubout -passin pass: -in "$1" 2> /dev/null); then
        echo "That private key doesn't belong to the certificate. Use the two from the same Cloudflare page."
        return 1
    fi
}

ask_certificate() {
    local status _
    for _ in 1 2 3; do
        if [ -f "$nginx_secrets/origin-cert.pem" ]; then
            echo "Paste the Origin certificate, from -----BEGIN CERTIFICATE----- to -----END CERTIFICATE-----, or type keep to keep the current one. It ends by itself at its END line; press Enter if it doesn't."
        else
            echo "Paste the Origin certificate, from -----BEGIN CERTIFICATE----- to -----END CERTIFICATE-----. It ends by itself at its END line; press Enter if it doesn't."
        fi
        status=0
        paste_pem CERTIFICATE "$answers_dir/origin-cert.pem" || status=$?
        case $status in
            0)
                if certificate_ok "$answers_dir/origin-cert.pem"; then
                    new_cert=yes
                    return 0
                fi
                ;;
            3)
                if [ -f "$nginx_secrets/origin-cert.pem" ]; then
                    new_cert=no
                    rm -f "$answers_dir/origin-cert.pem"
                    echo "Kept the current certificate, which ends $(cert_end "$nginx_secrets/origin-cert.pem" || echo "on an unknown date")."
                    return 0
                fi
                echo "There's no current certificate to keep yet. Paste the new one."
                ;;
            4) echo "That paste is longer than a certificate and has no END line. Paste the certificate again." ;;
            5) die "No certificate came in time, or the input ended. Nothing was changed." ;;
            *) echo "That doesn't start with -----BEGIN CERTIFICATE-----. Paste the certificate from Cloudflare again." ;;
        esac
    done
    die "Three answers in a row weren't right. Nothing was changed."
}

ask_key() {
    local status _
    for _ in 1 2 3; do
        echo "Paste its private key, the block Cloudflare showed below the certificate, from its BEGIN line to its END line. It isn't shown as you paste, and it ends by itself at its END line; press Enter if it doesn't."
        status=0
        paste_pem "PRIVATE KEY" "$answers_dir/origin-key.pem" hidden || status=$?
        case $status in
            0)
                echo "(The key arrived.)"
                if key_ok "$answers_dir/origin-key.pem" "$answers_dir/origin-cert.pem"; then
                    return 0
                fi
                ;;
            3) echo "A new certificate needs its own key: paste the key Cloudflare showed with it." ;;
            4) echo "That paste is longer than a key and has no END line. Paste the key again." ;;
            5) die "No key came in time, or the input ended. Nothing was changed." ;;
            *) echo "That doesn't start with a private key's BEGIN line. Paste the key from Cloudflare again." ;;
        esac
    done
    die "Three answers in a row weren't right. Nothing was changed."
}

# ---- Values both parts use

current() {   # current <name>: its value in dashboard.env, or nothing when it's missing or breaks the safe-character rule
    local value
    if value=$(env_value "$env_file" "$1" 2> /dev/null); then
        printf '%s\n' "$value"
    else
        echo "Note: $1 in dashboard.env breaks the safe-character rule, so it can't be kept." >&2
    fi
}

current_password() {   # the Pwd= part of dashboard.env's DefaultConnection, when it's one setup could have written
    local cs
    cs=$(current ConnectionStrings__DefaultConnection 2> /dev/null)
    if [[ $cs =~ (^|;)Pwd=([A-Za-z0-9]{40})(;|$) ]]; then
        printf '%s\n' "${BASH_REMATCH[2]}"
    fi
}

connection_string() {   # connection_string <database> <password>
    printf 'Server=127.0.0.1;Port=3306;Database=%s;Uid=%s;Pwd=%s;SslMode=Disabled;Connection Timeout=15;CharSet=utf8mb4;Maximum Pool Size=6;' \
        "$1" "$dashboard_login" "$2"
}

proxy_key_line() {   # proxy_key_line <key>: proxy-key.conf's one line
    printf 'proxy_set_header X-MinerBot-Proxy-Key "%s";\n' "$1"
}

login_hosts() {   # how many of the login's two hosts exist, 0 to 2
    run_for 30 mariadb -N -e "SELECT COUNT(*) FROM mysql.user WHERE User = '$dashboard_login' AND Host IN ('localhost', '127.0.0.1')"
}

# Whether the dashboard's login opens a database with this password, over TCP as the dashboard does. The password goes in
# on stdin, as an option file, never on a command line.
password_works() {   # password_works <password> <database>
    printf '[client]\nuser=%s\npassword=%s\nhost=127.0.0.1\nprotocol=tcp\n' "$dashboard_login" "$1" \
        | run_for 15 mariadb --defaults-file=/dev/stdin -D "$2" -N -e 'SELECT 1' > /dev/null
}

# Writes the candidate dashboard.env: setup's two header lines and its ten settings, then every other line of the current
# file, in its order, so lines the owner added under other names are kept. Run on its own output, it writes the same file.
candidate_env() {   # candidate_env <proxy key> <password> <file to write>
    local key=$1 pw=$2 out=$3 line i
    local -a values
    values=("https://$public_host" "$key" "$owners" "$admins" "$client_id" "$client_secret" "$team" "$aud"
        "$(connection_string "$SITE_DB" "$pw")" "$(connection_string "$BOT_DB" "$pw")")
    for i in "${!managed[@]}"; do
        env_safe "${values[$i]}" \
            || die "The value for ${managed[$i]} breaks the safe-character rule ($env_rule). Nothing was changed."
    done
    {
        printf '%s\n' "$env_header_1" "$env_header_2"
        for i in "${!managed[@]}"; do
            printf '%s=%s\n' "${managed[$i]}" "${values[$i]}"
        done
        if [ -f "$env_file" ]; then
            while IFS= read -r -t 5 line || [ -n "$line" ]; do
                if [ "$line" = "$env_header_1" ] || [ "$line" = "$env_header_2" ] || is_managed_line "$line"; then
                    continue
                fi
                printf '%s\n' "$line"
            done < "$env_file"
        fi
    } > "$out"
    chmod 600 "$out"
}

is_managed_line() {   # is_managed_line <line>: true for a NAME=value line of one of setup's ten settings
    local name
    [[ $1 == *=* ]] || return 1
    for name in "${managed[@]}"; do
        if [[ $1 == "$name="* ]]; then
            return 0
        fi
    done
    return 1
}

confirm() {
    local reply=
    [ "$yes" = no ] || return 0
    on_terminal || die "Nothing was changed: without a terminal, add --yes to go ahead."
    IFS= read -r -t "$(read_limit)" -p "Go ahead? Type yes: " reply || die "No answer came in time. Nothing was changed."
    [ "$reply" = yes ] || die "Stopped. Nothing was changed."
}

# ---- The foreground part: the checks and the questions, which change nothing

# shellcheck disable=SC2317,SC2329 # the EXIT trap runs it
foreground_cleanup() {
    if on_terminal; then stty echo 2> /dev/null || true; fi
    # The answers belong to the unit once it has started; it deletes them itself
    if [ -n "${answers_dir:-}" ] && { [ "${detached_unit_started:-no}" != yes ] || ! unit_running minerbot-setup; }; then
        rm -rf "$answers_dir"
    fi
}

foreground_checks() {   # checks 4 to 9; 1 to 3 come first, in foreground_part
    local n others
    step="looking for a dashboard image"
    if [ -z "$(image_id minerbot-dashboard:current)" ] && [ -z "$(image_id minerbot-dashboard:new)" ]; then
        die "There's no dashboard image yet. Run ./deploy.sh main --only dashboard --no-start first. Nothing was changed."
    fi
    step="checking the databases"
    load_server_conf
    n=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$BOT_DB' AND table_name = 'dashboard_sessions'") \
        || die "MariaDB didn't answer (above). Nothing was changed."
    [ "$n" = 1 ] \
        || die "MinerBot's database has no dashboard tables yet. Run ./deploy.sh main --only dashboard --no-start first: it makes them. Nothing was changed."
    n=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = '$SITE_DB' AND table_name IN ('ihvph_armor_styles', 'ihvph_blacklist', 'ihvph_discord_servers')") \
        || die "MariaDB didn't answer (above). Nothing was changed."
    [ "$n" = 3 ] \
        || die "The website's database, SITE_DB in server.conf, doesn't hold the armor, blacklist and Discord server tables. Check the name. Nothing was changed."
    step="checking nginx"
    # A change cut between two renames can itself be why nginx -t fails, so with a marker the unit undoes it first
    if [ ! -e "$home/state/nginx-change" ]; then
        run_for 30 nginx -t -q || die "nginx -t fails already, before this script changed anything (above). Fix that first. Nothing was changed."
    fi
    [ -f "$cloudflare_list" ] \
        || die "cPanel's list of Cloudflare's addresses, $cloudflare_list, is missing, and the dashboard's nginx file relies on it. Nothing was changed."
    n=$(grep -c '^[[:space:]]*set_real_ip_from[[:space:]]' "$cloudflare_list") || n=0
    if [ "$n" -lt 10 ] || ! grep -q '^[[:space:]]*real_ip_header[[:space:]]\{1,\}CF-Connecting-IP;' "$cloudflare_list"; then
        die "cPanel's list of Cloudflare's addresses, $cloudflare_list, has $n set_real_ip_from lines or no real_ip_header CF-Connecting-IP line, and the dashboard's nginx file relies on it. Nothing was changed."
    fi
    others=$(grep -rlF --exclude-dir=minerbot --exclude='.minerbot-*' -- "$public_host" /etc/nginx 2> /dev/null \
        | grep -vxF "$nginx_conf") || others=
    [ -z "$others" ] \
        || die "Another nginx file names $public_host, which means the host was added in cPanel: ${others//$'\n'/, }. Remove it there first; this host is never added in cPanel. Nothing was changed."
    [ -f "$home/minerbot-dashboard.conf" ] \
        || die "$home/minerbot-dashboard.conf is missing. deploy.sh installs it: run ./deploy.sh main --only dashboard --no-start first. Nothing was changed."
}

shown_or_none() {   # the current value to show; "none" for an empty one in an existing dashboard.env, so Enter keeps it
    if [ -n "$1" ]; then
        printf '%s\n' "$1"
    elif [ -f "$env_file" ]; then
        echo none
    fi
}

refuse_kept() {   # refuse_kept <setting>
    die "--no-questions keeps the current values, but $1 in dashboard.env is missing or isn't right. Run ./setup-dashboard.sh without --no-questions to give it. Nothing was changed."
}

gather_answers() {
    cur_client_id=$(current Discord__ClientId)
    cur_secret=$(current Discord__ClientSecret)
    cur_owners=$(current Dashboard__Owners)
    cur_admins=$(current Dashboard__Admins)
    cur_team=$(current CloudflareAccess__TeamDomain)
    cur_aud=$(current CloudflareAccess__Audience)
    answers_dir=$(mktemp -d "$home/state/tmp/setup.XXXXXX")
    new_cert=no
    if [ "$mode" = questions ]; then
        step="asking the questions"
        ask_certificate
        if [ "$new_cert" = yes ]; then
            ask_key
        fi
        ask "MinerBot Dashboard's client ID" "$cur_client_id" check_client_id
        client_id=$answer
        ask "Its client secret (it isn't shown)" "$cur_secret" check_secret hidden
        client_secret=$answer
        ask "The owners' Discord user IDs, separated by commas" "$cur_owners" check_owners
        owners=$answer
        ask "The admins' Discord user IDs, separated by commas, or none" "$(shown_or_none "$cur_admins")" check_admins
        admins=$answer
        ask "The Cloudflare Access team domain, such as yourteam.cloudflareaccess.com, or none to leave Access off" \
            "$(shown_or_none "$cur_team")" check_team
        team=$answer
        aud=
        if [ -n "$team" ]; then
            ask "The Access application's AUD tag" "$cur_aud" check_aud
            aud=$answer
        fi
        return 0
    fi
    step="checking the current values"
    if [ ! -f "$nginx_secrets/origin-cert.pem" ] || [ ! -f "$nginx_secrets/origin-key.pem" ]; then
        die "--no-questions keeps the current certificate, and nginx has none yet. Run ./setup-dashboard.sh without --no-questions to paste one. Nothing was changed."
    fi
    check_client_id "$cur_client_id" > /dev/null || refuse_kept Discord__ClientId
    client_id=$answer
    check_secret "$cur_secret" > /dev/null || refuse_kept Discord__ClientSecret
    client_secret=$answer
    check_owners "$cur_owners" > /dev/null || refuse_kept Dashboard__Owners
    owners=$answer
    check_admins "$cur_admins" > /dev/null || refuse_kept Dashboard__Admins
    admins=$answer
    check_team "$cur_team" > /dev/null || refuse_kept CloudflareAccess__TeamDomain
    team=$answer
    aud=
    if [ -n "$team" ]; then
        check_aud "$cur_aud" > /dev/null || refuse_kept CloudflareAccess__Audience
        aud=$answer
    elif [ -n "$cur_aud" ]; then
        refuse_kept CloudflareAccess__Audience
    fi
}

# What the unit will change, worked out ahead for the summary, with setting names and never values. The unit works it
# out again itself before it changes anything.
plan_and_summarize() {
    local cur_key cur_pw hosts name served expected
    local -a changed=() lines=()
    local -A new=()
    step="working out what changes"
    cur_key=$(current Dashboard__ProxyKey)
    key_changes=no
    if [ "$rotate_key" = yes ] || ! [[ $cur_key =~ ^[0-9a-f]{64}$ ]]; then
        key_changes=yes
    fi
    hosts=$(login_hosts) || die "MariaDB didn't answer (above). Nothing was changed."
    cur_pw=$(current_password)
    pw_changes=no
    if [ "$hosts" != 2 ] || [ "$rotate_pw" = yes ] || [ -z "$cur_pw" ] || ! password_works "$cur_pw" "$BOT_DB" 2> /dev/null; then
        pw_changes=yes
    fi
    new=([Dashboard__PublicOrigin]="https://$public_host" [Dashboard__Owners]="$owners" [Dashboard__Admins]="$admins"
        [Discord__ClientId]="$client_id" [Discord__ClientSecret]="$client_secret" [CloudflareAccess__TeamDomain]="$team"
        [CloudflareAccess__Audience]="$aud" [Dashboard__ProxyKey]="$cur_key"
        [ConnectionStrings__DefaultConnection]="$(connection_string "$SITE_DB" "$cur_pw")"
        [ConnectionStrings__BotConnection]="$(connection_string "$BOT_DB" "$cur_pw")")
    for name in "${managed[@]}"; do
        if [ "$name" = Dashboard__ProxyKey ] && [ "$key_changes" = yes ]; then
            changed+=("$name")
        elif [[ $name == ConnectionStrings__* ]] && [ "$pw_changes" = yes ]; then
            changed+=("$name")
        elif [ "$(current "$name" 2> /dev/null)" != "${new[$name]}" ] || ! grep -q "^$name=" "$env_file" 2> /dev/null; then
            changed+=("$name")
        fi
    done

    nginx_changes=no
    if [ "$new_cert" = yes ] || [ "$key_changes" = yes ] || ! cmp -s "$home/minerbot-dashboard.conf" "$nginx_conf" \
        || [ ! -f "$nginx_secrets/origin-key.pem" ] || [ ! -f "$nginx_secrets/proxy-key.conf" ] \
        || [ "$(< "$nginx_secrets/proxy-key.conf")" != "$(proxy_key_line "$cur_key")" ]; then
        nginx_changes=yes
    fi
    reload_only=no
    if [ "$nginx_changes" = no ]; then
        served=$(served_fingerprint 2> /dev/null) || served=
        expected=$(run_for 10 openssl x509 -noout -fingerprint -sha256 -in "$nginx_secrets/origin-cert.pem" 2> /dev/null) || expected=
        if [ -n "$served" ] && [ "$served" != "$expected" ]; then
            reload_only=yes
        fi
    fi
    container_exists=no
    if [ -n "$(container_image "$dashboard_container")" ]; then
        container_exists=yes
    fi

    if [ -e "$home/state/nginx-change" ]; then
        lines+=("undo the nginx change that was cut off, from its backup, then test and reload nginx")
    fi
    if [ "$new_cert" = yes ]; then
        lines+=("install the new certificate and its key for nginx")
    fi
    if [ ${#changed[@]} -gt 0 ]; then
        printf -v name '%s, ' "${changed[@]}"
        lines+=("change these settings in dashboard.env: ${name%, }")
    fi
    if [ "$hosts" != 2 ]; then
        lines+=("create the dashboard's database login, $dashboard_login, on localhost and 127.0.0.1, with a new password")
    elif [ "$pw_changes" = yes ]; then
        lines+=("give the dashboard's database login a new password")
    fi
    lines+=("apply the login's database rights again, after a rehearsal on a throwaway login, and compare them")
    if [ "$nginx_changes" = yes ]; then
        if [ "$key_changes" = yes ] && [ "$container_exists" = yes ]; then
            lines+=("stop the dashboard first, so visitors see nginx's restarting page while the proxy key changes")
        fi
        lines+=("test nginx's files on their own and with every site, and reload nginx for every site on this server only if both tests pass")
    elif [ "$reload_only" = yes ]; then
        lines+=("reload nginx, which serves an older copy of the certificate")
    fi
    if [ ${#changed[@]} -gt 0 ] && [ "$container_exists" = yes ]; then
        lines+=("recreate the dashboard with the new settings: about 10 seconds of the restarting page")
    elif [ ${#changed[@]} -gt 0 ]; then
        lines+=("leave the dashboard to the next deploy, which starts it: it isn't running yet")
    fi
    echo
    echo "Ready to:"
    printf -- '- %s\n' "${lines[@]}"
    echo "Nothing else changes. Running this again later changes only what differs then."
}

hand_over() {   # writes the answers for the unit, then starts it and follows it
    {
        printf 'MODE=%s\n' "$mode"
        printf 'NEW_CERT=%s\n' "${new_cert:-no}"
        printf 'CLIENT_ID=%s\n' "${client_id:-}"
        printf 'CLIENT_SECRET=%s\n' "${client_secret:-}"
        printf 'OWNERS=%s\n' "${owners:-}"
        printf 'ADMINS=%s\n' "${admins:-}"
        printf 'TEAM=%s\n' "${team:-}"
        printf 'AUD=%s\n' "${aud:-}"
        printf 'ROTATE_PROXY_KEY=%s\n' "$rotate_key"
        printf 'ROTATE_DB_PASSWORD=%s\n' "$rotate_pw"
    } > "$answers_dir/answers"
    chmod 600 "$answers_dir/answers"
    step="starting the changes"
    if run_detached setup 1800 "$home/setup-dashboard.sh" --apply "$answers_dir"; then
        exit 0
    fi
    exit 1
}

foreground_part() {
    step="checking before any question"
    [ "$(id -u)" = 0 ] || die "Run this as root. Nothing was changed."
    cd "$home"
    if [ -e "$home/state/nginx-change" ]; then
        echo "Note: an nginx change was cut off earlier. The changes undo it first, from its backup."
    fi
    lock_is_free || die "Another run holds the lock: $(lock_holder). Wait for it to finish, then run this again. Nothing was changed."
    install -d -m 700 "$home/state" "$home/state/tmp"
    # Nothing runs while the lock is free, so whatever is in state/tmp was left by a run that was cut off
    find "$home/state/tmp" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
    trap 'status=$?; foreground_cleanup || true; exit "$status"' EXIT

    if [ "$mode" = remove-nginx ]; then
        if [ ! -e "$nginx_conf" ] && [ ! -e "$home/state/nginx-change" ]; then
            echo "MinerBot's file isn't in nginx. Nothing was changed."
            exit 0
        fi
        echo "Ready to take MinerBot's file out of nginx. It keeps a copy in $home/state/nginx-backup, tests nginx without the file, and reloads nginx for every site only if that test passes. If nginx -t still fails, the file goes back as it was. Nothing else changes, and $nginx_secrets stays."
        confirm
        answers_dir=$(mktemp -d "$home/state/tmp/setup.XXXXXX")
        hand_over
    fi

    foreground_checks
    gather_answers
    plan_and_summarize
    confirm
    hand_over
}

# ---- The detached part: the unit minerbot-setup, which writes only to its log

# shellcheck disable=SC2317,SC2329 # the EXIT trap runs it
detached_cleanup() {
    if [ -n "${work:-}" ]; then
        rm -rf "$work"
    fi
    case ${apply_dir:-} in
        "$home"/state/tmp/setup.*) rm -rf "$apply_dir" ;;
    esac
    rm -f "$home/dashboard.env.next"
    if [ "${rights_check_made:-}" = yes ]; then
        drop_rights_check
    fi
}

# What die does first in the unit: start the dashboard again on its old settings when this run stopped it and the
# settings didn't move, and say so when an nginx change is left cut off
before_stop() {
    [ "${run_mode:-}" = detached ] || return 0
    rm -f "$home/dashboard.env.next"
    if [ "${stopped_dashboard:-no}" = yes ] && [ "${env_moved:-no}" = no ]; then
        echo "Starting the dashboard again on its old settings."
        if start_service dashboard && ready_dashboard; then
            echo "The dashboard runs again on its old settings."
        else
            echo "The dashboard didn't get ready again (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
        fi
    fi
    if [ -e "$home/state/nginx-change" ]; then
        echo "An nginx change was left cut off. Run ./setup-dashboard.sh again: it undoes that first."
    fi
}

take_answers() {   # the answers the foreground left, taken into this run; then their folder is deleted
    local file name
    case $apply_dir in
        "$home"/state/tmp/setup.*) ;;
        *) die "--apply takes a folder setup-dashboard.sh made in $home/state/tmp. Nothing was changed." ;;
    esac
    file=$apply_dir/answers
    [ -f "$file" ] || die "The answers this run was started with are gone, so nothing was changed. Run ./setup-dashboard.sh again."
    mode=$(env_value "$file" MODE)
    case $mode in
        questions | no-questions | remove-nginx) ;;
        *) die "The answers this run was started with aren't whole, so nothing was changed. Run ./setup-dashboard.sh again." ;;
    esac
    work=$(mktemp -d "$home/state/tmp/apply.XXXXXX")
    if [ "$mode" != remove-nginx ]; then
        new_cert=$(env_value "$file" NEW_CERT)
        client_id=$(env_value "$file" CLIENT_ID)
        client_secret=$(env_value "$file" CLIENT_SECRET)
        owners=$(env_value "$file" OWNERS)
        admins=$(env_value "$file" ADMINS)
        team=$(env_value "$file" TEAM)
        aud=$(env_value "$file" AUD)
        rotate_key=$(env_value "$file" ROTATE_PROXY_KEY)
        rotate_pw=$(env_value "$file" ROTATE_DB_PASSWORD)
        if ! [[ $client_id =~ ^[0-9]{17,20}$ && $client_secret =~ ^[A-Za-z0-9_-]{16,128}$ && -n $owners ]] \
            || ! [[ $new_cert =~ ^(yes|no)$ && $rotate_key =~ ^(yes|no)$ && $rotate_pw =~ ^(yes|no)$ ]]; then
            die "The answers this run was started with aren't whole, so nothing was changed. Run ./setup-dashboard.sh again."
        fi
        if [ "$new_cert" = yes ]; then
            for name in origin-cert.pem origin-key.pem; do
                [ -f "$apply_dir/$name" ] || die "The certificate this run was started with is gone, so nothing was changed. Run ./setup-dashboard.sh again."
                cp -p "$apply_dir/$name" "$work/$name"
            done
        fi
    fi
    rm -rf "$apply_dir"
}

new_password() {   # 40 letters and digits, made as setup-login.sh made the bot's, so it meets the server's password rules
    local pw _
    for _ in 1 2 3 4 5; do
        pw=$(run_for 10 openssl rand -base64 64 | tr -dc 'A-Za-z0-9' | cut -c1-40) || pw=
        if [[ $pw =~ ^[A-Za-z0-9]{40}$ ]]; then
            printf '%s\n' "$pw"
            return 0
        fi
    done
    return 1
}

# nginx refused the new files and runs the old ones, which match the dashboard.env that didn't move. A dashboard this run
# stopped starts again on it. A database password this run changed can't be taken back, so then dashboard.env gets only
# the new password, and the dashboard is recreated with it.
nginx_refused() {
    rm -f "$home/dashboard.env.next"
    env_moved=yes
    if [ "$pw_changed" = yes ] && [ -f "$env_file" ]; then
        write_env "$env_file" ConnectionStrings__DefaultConnection "$(connection_string "$SITE_DB" "$pw")"
        write_env "$env_file" ConnectionStrings__BotConnection "$(connection_string "$BOT_DB" "$pw")"
        echo "The dashboard's database password changed in this run, so dashboard.env got the new one and nothing else."
        if [ "$container_exists" = yes ]; then
            if start_service dashboard --force-recreate && ready_dashboard; then
                echo "The dashboard runs again, with its old settings and the new password."
            else
                echo "The dashboard didn't get ready (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
            fi
        fi
        die "nginx refused the new files, so nginx and the dashboard's other settings are as they were."
    fi
    if [ "$stopped_dashboard" = yes ]; then
        if start_service dashboard && ready_dashboard; then
            die "nginx refused the new files, so nothing changed, and the dashboard runs again on its old settings."
        fi
        die "nginx refused the new files, so nothing changed, but the dashboard this run stopped didn't get ready again (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
    fi
    die "nginx refused the new files, so nothing changed."
}

not_ready_after_recreate() {
    local tmp
    if [ "$pw_changed" = no ] && [ "$key_changed" = no ] && [ -f "$home/dashboard.env.previous" ]; then
        echo "The dashboard wasn't ready with the new settings, so the earlier ones go back."
        tmp=$(mktemp "$home/.dashboard.env.XXXXXX")
        cp -p "$home/dashboard.env.previous" "$tmp"
        mv -f "$tmp" "$env_file"
        start_service dashboard --force-recreate || true
        if ready_dashboard; then
            die "The dashboard wasn't ready with the new settings (above), so it runs on the earlier ones again. Check the values you gave, then run ./setup-dashboard.sh again."
        fi
        die "The dashboard isn't ready with the earlier settings either (above), so the problem is outside its settings: the database or the server. Its log: docker logs --tail 50 $dashboard_container"
    fi
    die "The dashboard wasn't ready with the new settings (above). Its proxy key or database password changed in this run, so the earlier settings no longer work. Look at its log with: docker logs --tail 50 $dashboard_container. Fix the cause, then run ./setup-dashboard.sh --no-questions."
}

refresh_readme() {   # renders README.txt again from the installed deploy files' commit, for the certificate's end date
    local commit='' from out
    if [ -f "$home/state/deploy-files.commit" ]; then
        IFS= read -r -t 5 commit < "$home/state/deploy-files.commit" || true
    fi
    [[ $commit =~ ^[0-9a-f]{40}$ ]] || return 0
    from=$(mktemp "$home/state/tmp/readme-source.XXXXXX")
    out=$(mktemp "$home/state/tmp/readme.XXXXXX")
    if run_for 30 git -C "$home/src" show "$commit:deploy/README.txt" > "$from" 2> /dev/null; then
        render_readme "$from" > "$out"
        if ! cmp -s "$out" "$home/README.txt"; then
            chmod 600 "$out"
            mv -f "$out" "$home/README.txt"
            echo "README.txt has the certificate's end date now."
        fi
    else
        echo "Note: README.txt wasn't rendered again: git couldn't show it from ${commit:0:12}. The next deploy does it."
    fi
    rm -f "$from" "$out"
}

detached_part() {
    local cur_key cur_pw host n image served expected ends sql name joined
    local -a missing_hosts=() changes=() problems=()
    if [ "${MINERBOT_DETACHED:-}" != 1 ] || [ -z "${MINERBOT_LOG:-}" ]; then
        die "--apply is only for the run setup-dashboard.sh starts itself. Nothing was changed."
    fi
    exec >> "$MINERBOT_LOG" 2>&1
    run_mode=detached
    step="taking the lock"
    take_lock setup
    trap 'status=$?; detached_cleanup || true; exit "$status"' EXIT
    cd "$home"
    echo "Setup's changes, as the unit minerbot-setup, $(date -u +%Y-%m-%dT%H:%M:%SZ), with $(time_left) seconds left."
    step="taking the answers"
    take_answers

    if [ "$mode" = remove-nginx ]; then
        step="taking MinerBot's file out of nginx"
        nginx_remove
        case $nginx_result in
            absent | removed) finish ok "nginx file: $nginx_result" ;;
            *) finish failed "nginx file: $nginx_result" ;;
        esac
    fi

    # 1. A change that was cut off is undone before anything else
    if [ -e "$home/state/nginx-change" ]; then
        step="undoing a cut-off nginx change"
        nginx_undo_cut_off
        case $nginx_result in
            undone) changes+=("cut-off nginx change undone") ;;
            none) ;;
            *) die "The nginx change that was cut off couldn't be undone cleanly (above), so nothing else was changed." ;;
        esac
    fi

    step="loading server.conf"
    load_server_conf

    # 2. The proxy key
    step="choosing the proxy key"
    cur_key=$(current Dashboard__ProxyKey)
    if [ "$rotate_key" = no ] && [[ $cur_key =~ ^[0-9a-f]{64}$ ]]; then
        key=$cur_key
        key_changed=no
    else
        key=$(run_for 10 openssl rand -hex 32) || die "openssl couldn't make a proxy key (above). Nothing was changed."
        [[ $key =~ ^[0-9a-f]{64}$ ]] || die "openssl made a proxy key of the wrong shape. Nothing was changed."
        key_changed=yes
        echo "A new proxy key was made. It's never shown."
    fi

    # 3. The database password
    step="checking the dashboard's database password"
    for host in localhost 127.0.0.1; do
        n=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM mysql.user WHERE User = '$dashboard_login' AND Host = '$host'") \
            || die "MariaDB didn't answer (above). Nothing was changed."
        if [ "$n" != 1 ]; then
            missing_hosts+=("$host")
        fi
    done
    cur_pw=$(current_password)
    if [ ${#missing_hosts[@]} -eq 0 ] && [ "$rotate_pw" = no ] && [ -n "$cur_pw" ] && password_works "$cur_pw" "$BOT_DB"; then
        pw=$cur_pw
        pw_changed=no
        echo "The dashboard's database password works, so it stays."
    else
        pw=$(new_password) || die "openssl couldn't make a password (above). Nothing was changed."
        pw_changed=yes
        echo "The dashboard's database login gets a new password. It's never shown."
    fi

    # 4. The candidate dashboard.env
    step="writing the candidate dashboard.env"
    candidate_env "$key" "$pw" "$home/dashboard.env.next"

    # 5. The dashboard's own validator, before the database or nginx is touched
    step="checking the new settings"
    image=minerbot-dashboard:current
    if [ -z "$(image_id "$image")" ]; then
        image=minerbot-dashboard:new
    fi
    check_settings "$home/dashboard.env.next" "$image" \
        || die "The dashboard's own check refused the new settings (above). Nothing was changed."

    # 6. The login, its rights, and its password on both databases
    step="creating or changing the dashboard's database login"
    sql=
    for host in localhost 127.0.0.1; do
        if [[ " ${missing_hosts[*]} " == *" $host "* ]]; then
            sql+="CREATE USER '$dashboard_login'@'$host' IDENTIFIED BY '$pw';"$'\n'
        elif [ "$pw_changed" = yes ]; then
            sql+="ALTER USER '$dashboard_login'@'$host' IDENTIFIED BY '$pw';"$'\n'
        fi
    done
    if [ -n "$sql" ]; then
        # On stdin, never on a command line, and with --skip-print-query-on-error, without which MariaDB's client prints a
        # failing statement, password included
        run_for 30 mariadb --batch --skip-print-query-on-error <<< "$sql" \
            || die "Creating the dashboard's database login or changing its password failed (above). Run ./setup-dashboard.sh again to finish: it makes the rest agree."
        sql=
        if [ ${#missing_hosts[@]} -gt 0 ]; then
            changes+=("login created")
        else
            changes+=("new database password")
        fi
    fi
    if [ "$pw_changed" = yes ]; then
        stop_hint="The dashboard's database password changed in this run: run ./setup-dashboard.sh again to finish, and it makes the rest agree."
    fi
    step="applying the dashboard's database rights"
    apply_rights "$home/dashboard-rights.sql" || die "The dashboard's database rights didn't apply (above)."
    step="testing the dashboard's password on both databases"
    if ! password_works "$pw" "$SITE_DB" || ! password_works "$pw" "$BOT_DB"; then
        die "The dashboard's login can't open both databases with its password (above)."
    fi
    echo "The dashboard's login opens both databases with its password."

    # 7. What else needs to change
    step="comparing nginx's files"
    install -d -m 700 "$work/nginx"
    cp "$home/minerbot-dashboard.conf" "$work/nginx/minerbot-dashboard.conf"
    proxy_key_line "$key" > "$work/nginx/proxy-key.conf"
    if [ "$new_cert" = yes ]; then
        cp "$work/origin-cert.pem" "$work/origin-key.pem" "$work/nginx/"
    elif [ -f "$nginx_secrets/origin-cert.pem" ] && [ -f "$nginx_secrets/origin-key.pem" ]; then
        cp "$nginx_secrets/origin-cert.pem" "$nginx_secrets/origin-key.pem" "$work/nginx/"
    else
        die "nginx has no certificate for the dashboard yet. Run ./setup-dashboard.sh and paste one."
    fi
    nginx_files_change=no
    for name in "${nginx_names[@]}"; do
        if ! cmp -s "$work/nginx/$name" "$(nginx_target "$name")"; then
            nginx_files_change=yes
        fi
    done
    reload_only=no
    if [ "$nginx_files_change" = no ]; then
        served=$(served_fingerprint 2> /dev/null) || served=
        expected=$(run_for 10 openssl x509 -noout -fingerprint -sha256 -in "$nginx_secrets/origin-cert.pem" 2> /dev/null) || expected=
        if [ -n "$served" ] && [ "$served" != "$expected" ]; then
            reload_only=yes
        fi
    fi
    env_changed=no
    if ! cmp -s "$home/dashboard.env.next" "$env_file"; then
        env_changed=yes
    fi
    container_exists=no
    if [ -n "$(container_image "$dashboard_container")" ]; then
        container_exists=yes
    fi

    # 8. In this order
    stopped_dashboard=no
    env_moved=no
    if [ "$key_changed" = yes ] && [ "$container_exists" = yes ]; then
        step="stopping the dashboard"
        echo "Stopping the dashboard while the proxy key changes, so visitors see nginx's restarting page."
        compose 60 stop dashboard
        stopped_dashboard=yes
    fi
    if [ "$nginx_files_change" = yes ]; then
        step="changing nginx's files"
        nginx_change "$work/nginx"
        case $nginx_result in
            refused) nginx_refused ;;
            problem) problems+=("$nginx_problem") ;;
        esac
        changes+=("nginx files")
    elif [ "$reload_only" = yes ]; then
        step="reloading nginx"
        if run_for 30 nginx -t -q && run_for 120 /scripts/restartsrv_nginx --reload; then
            echo "nginx reloaded: it served an older copy of the certificate."
            changes+=("nginx reloaded")
        else
            problems+=("nginx serves another certificate than the installed one, and the reload that would fix it failed (above).")
        fi
    fi
    step="moving dashboard.env"
    if [ "$env_changed" = yes ]; then
        if [ -f "$env_file" ]; then
            mv -f "$env_file" "$home/dashboard.env.previous"
        fi
        mv -f "$home/dashboard.env.next" "$env_file"
        env_moved=yes
        changes+=("dashboard.env")
    else
        rm -f "$home/dashboard.env.next"
    fi
    if [ "$env_changed" = yes ] && [ "$container_exists" = yes ]; then
        step="recreating the dashboard"
        echo "Recreating the dashboard with its new settings."
        start_service dashboard --force-recreate || true
        if ! ready_dashboard; then
            not_ready_after_recreate
        fi
        changes+=("dashboard recreated")
    elif [ "$stopped_dashboard" = yes ]; then
        start_service dashboard || true
        ready_dashboard || die "The dashboard isn't ready after nginx's change (above)."
    fi

    # 9. README.txt, the history, and the summary
    step="rendering README.txt"
    refresh_readme
    echo
    joined="no change"
    if [ ${#changes[@]} -gt 0 ]; then
        printf -v joined '%s, ' "${changes[@]}"
        joined=${joined%, }
        echo "Done. What changed: $joined."
    else
        echo "Done. Nothing differed: the rights were applied again, and the password works."
    fi
    if ends=$(cert_end "$nginx_secrets/origin-cert.pem"); then
        echo "The Origin certificate ends $ends: make a new one in Cloudflare before then and run ./setup-dashboard.sh. Put the date in your calendar."
    fi
    if [ "$container_exists" = no ]; then
        echo "Next: ./deploy.sh main --only dashboard, which starts the dashboard."
    fi
    if [ ${#problems[@]} -gt 0 ]; then
        printf 'Problem: %s\n' "${problems[@]}"
        finish failed "$joined, with an nginx problem"
    fi
    finish ok "$joined"
}

if [ -n "$apply_dir" ]; then
    detached_part
else
    foreground_part
fi

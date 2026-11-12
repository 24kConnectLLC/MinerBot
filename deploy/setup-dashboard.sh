#!/usr/bin/env bash
# Sets up MinerBot's web dashboard, and changes its settings later. The owner runs it as root, in WHM's Terminal or over
# SSH. It asks only for what only the owner has: the Cloudflare Origin certificate and its private key, the MinerBot
# Dashboard application's client ID and secret, the owners' and admins' Discord user IDs, and Cloudflare Access's team
# domain and AUD tag when Access is on. It makes the proxy key and the dashboard's database password itself and never
# shows them. The answers are checked here; the changes run as a systemd unit of their own, minerbot-setup, which this
# terminal follows. They change only what differs, so running this again finishes whatever an interruption left.
# Usage:
#   ./setup-dashboard.sh                         asks every question; Enter keeps a value, and keep keeps a paste
#   ./setup-dashboard.sh --no-questions          keeps every current value, installs a changed nginx file once the
#                                                dashboard of its commit runs, applies the rights the last apply used
#                                                again, repairs whatever differs and starts a dashboard that's stopped,
#                                                then asks "Go ahead?"
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
# The copy apply_rights keeps of the rights file it last applied. Setup applies that copy again, so it never gives the
# running dashboard rights from a newer commit than the last dashboard deploy applied: a --only bot deploy installs a
# newer dashboard-rights.sql without applying it. Only before any apply has kept one, on the first setup, does setup
# apply the installed dashboard-rights.sql.
rights_copy=$home/state/dashboard-rights.applied.sql
env_header_1="# The web dashboard's settings. setup-dashboard.sh writes this file: run it again to change them."
env_header_2="# It keeps lines under other names. The dashboard reads the file only when its container is created."
managed=(Dashboard__PublicOrigin Dashboard__ProxyKey Dashboard__Owners Dashboard__Admins Discord__ClientId
    Discord__ClientSecret CloudflareAccess__TeamDomain CloudflareAccess__Audience ConnectionStrings__DefaultConnection
    ConnectionStrings__BotConnection)
# The end of a message that stops the unit before its own changes. Once its first step has undone a cut-off nginx
# change, which can put nginx's files back and reload nginx, it names that undo instead.
unchanged="Nothing was changed."

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

# The certificate isn't secret, but it's read hidden too: a private key pasted here by mistake, alone or after the
# certificate in one paste, must never reach the screen. certificate_ok shows what arrived instead.
ask_certificate() {
    local status _
    for _ in 1 2 3; do
        if [ -f "$nginx_secrets/origin-cert.pem" ]; then
            echo "Paste the Origin certificate, from -----BEGIN CERTIFICATE----- to -----END CERTIFICATE-----, or type keep to keep the current one. It isn't shown as you paste or type, and it ends by itself at its END line; press Enter if it doesn't."
        else
            echo "Paste the Origin certificate, from -----BEGIN CERTIFICATE----- to -----END CERTIFICATE-----. It isn't shown as you paste, and it ends by itself at its END line; press Enter if it doesn't."
        fi
        status=0
        paste_pem CERTIFICATE "$answers_dir/origin-cert.pem" hidden || status=$?
        case $status in
            0)
                echo "(The certificate arrived.)"
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
            *)
                if [[ $pem_begin == *"PRIVATE KEY" ]]; then
                    echo "That was a private key, and it wasn't shown. Paste the certificate first: the key comes next, at its own prompt."
                else
                    echo "That doesn't start with -----BEGIN CERTIFICATE-----. Paste the certificate from Cloudflare again."
                fi
                ;;
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

# The proxy key nginx sends, from proxy-key.conf, when its line is one setup wrote; nothing otherwise. Once a cut-off
# change is undone, nginx holds only a key a whole nginx change put there, so a key there that differs from
# dashboard.env's is newer: a run was cut off after nginx's change, before dashboard.env moved. dashboard.env takes it,
# so a key that was rotated because it may have leaked never goes back into nginx.
nginx_proxy_key() {
    local line='' shape='^proxy_set_header X-MinerBot-Proxy-Key "([0-9a-f]{64})";$'
    [ -f "$nginx_secrets/proxy-key.conf" ] || return 0
    IFS= read -r -t 5 line < "$nginx_secrets/proxy-key.conf" || true
    if [[ $line =~ $shape ]]; then
        printf '%s\n' "${BASH_REMATCH[1]}"
    fi
}

# The file setup puts into nginx for the dashboard, in conf_source: the reference copy deploy.sh installed, unless it
# waits for the dashboard of its own commit (nginx_file_waits) while nginx has a file for the dashboard already. Then
# nginx keeps its own, and conf_note says why when the two differ. With no file in nginx yet, the reference goes in.
nginx_conf_source() {
    conf_source=$home/minerbot-dashboard.conf
    conf_note=
    if [ -f "$nginx_conf" ] && nginx_file_waits; then
        conf_source=$nginx_conf
        if ! cmp -s "$home/minerbot-dashboard.conf" "$nginx_conf"; then
            conf_note="nginx keeps its current file for the dashboard: the reference copy is from ${files_commit:0:12}, but the dashboard's image is from ${dashboard_rev:0:12}, and nginx's file moves only with the dashboard. Once ./deploy.sh has deployed the dashboard of ${files_commit:0:12}, it says to run ./setup-dashboard.sh --no-questions, which installs it."
        fi
    fi
}

# True when pipelog.conf or cPanel's hook isn't what an nginx change writes, as on a server set up before they existed,
# or after WHM's piped logging changed while the hook was missing. Every nginx change writes both, so one is due then.
pipelog_stale() {
    ! cmp -s <(pipelog_text) "$nginx_secrets/pipelog.conf" \
        || { [ -d "${pipelog_hook%/*}" ] && ! cmp -s <(pipelog_hook_text) "$pipelog_hook"; }
}

# True when the dashboard's container runs on other values of setup's ten settings than dashboard.env holds: a recreate
# that failed left the old container running, or a run was cut off after dashboard.env moved, before its recreate.
# Docker reads the env file only when it makes a container, so only a recreate brings it in step. False when they agree,
# or when Docker or dashboard.env can't tell; a setting dashboard.env doesn't name isn't compared. The values are only
# compared, never printed.
container_env_stale() {
    local vars name value line
    vars=$(run_for 30 docker container inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$dashboard_container" 2> /dev/null) \
        || return 1
    if [ -z "$vars" ] || [ ! -f "$env_file" ]; then
        return 1
    fi
    for name in "${managed[@]}"; do
        grep -q "^$name=" "$env_file" || continue
        value=$(env_value "$env_file" "$name" 2> /dev/null) || return 1
        line=$(grep -m 1 "^$name=" <<< "$vars") || line=
        if [ "$line" != "$name=$value" ]; then
            return 0
        fi
    done
    return 1
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
            || die "The value for ${managed[$i]} breaks the safe-character rule ($env_rule). $unchanged"
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
    local state=0
    if on_terminal; then stty echo 2> /dev/null || true; fi
    # The answers belong to the unit once it has started; it deletes them itself. So they go only when it never started,
    # or once systemd says it doesn't run: a systemctl that answers nothing says nothing either way.
    if [ -n "${answers_dir:-}" ] && [ "${detached_unit_started:-no}" = yes ]; then
        unit_running minerbot-setup || state=$?
    fi
    if [ -n "${answers_dir:-}" ] && { [ "${detached_unit_started:-no}" != yes ] || [ "$state" = 1 ]; }; then
        rm -rf "$answers_dir"
    fi
}

foreground_checks() {   # checks 4 to 9; 1 to 3 come first, in foreground_part
    local n others current_id new_id
    step="looking for a dashboard image"
    current_id=$(image_id minerbot-dashboard:current)
    new_id=$(image_id minerbot-dashboard:new)
    if [ -z "$current_id" ] && [ -z "$new_id" ]; then
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
    local cur_key plan_key sent_key='' cur_pw hosts name served expected rights running
    local -a changed=() lines=()
    local -A new=()
    step="working out what changes"
    cur_key=$(current Dashboard__ProxyKey)
    # The key the unit will use: dashboard.env's, nginx's newer one (nginx_proxy_key), or a new one, empty here. After a
    # cut-off nginx change the unit undoes it before it looks at nginx's key, so then this counts on dashboard.env's.
    plan_key=$cur_key
    key_changes=no
    if [ "$rotate_key" = yes ] || ! [[ $cur_key =~ ^[0-9a-f]{64}$ ]]; then
        key_changes=yes
        plan_key=
    elif [ ! -e "$home/state/nginx-change" ]; then
        sent_key=$(nginx_proxy_key)
        if [ -n "$sent_key" ] && [ "$sent_key" != "$cur_key" ]; then
            key_changes=yes
            plan_key=$sent_key
        fi
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

    nginx_conf_source
    nginx_changes=no
    if [ "$new_cert" = yes ] || [ -z "$plan_key" ] || ! cmp -s "$conf_source" "$nginx_conf" \
        || [ ! -f "$nginx_secrets/origin-key.pem" ] || [ ! -f "$nginx_secrets/proxy-key.conf" ] \
        || [ "$(< "$nginx_secrets/proxy-key.conf")" != "$(proxy_key_line "$plan_key")" ] || pipelog_stale; then
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
    # true or false for a container that exists, nothing when there's none
    running=$(container_running "$dashboard_container")
    container_exists=no
    if [ -n "$running" ]; then
        container_exists=yes
    fi

    if [ -e "$home/state/nginx-change" ]; then
        lines+=("undo the nginx change that was cut off, from its backup, then test and reload nginx")
    fi
    if [ "$new_cert" = yes ]; then
        lines+=("install the new certificate and its key for nginx")
    fi
    if [ -n "$sent_key" ] && [ "$sent_key" != "$cur_key" ] && [ "$plan_key" = "$sent_key" ]; then
        lines+=("give dashboard.env the proxy key nginx sends, which is newer: a run was cut off after nginx's change, before dashboard.env moved")
    fi
    if [ ${#changed[@]} -gt 0 ]; then
        printf -v name '%s, ' "${changed[@]}"
        lines+=("change these settings in dashboard.env: ${name%, }")
    fi
    if [ -n "$conf_note" ]; then
        lines+=("keep nginx's current file for the dashboard: the reference copy is from ${files_commit:0:12}, but the dashboard's image is from ${dashboard_rev:0:12}, so it waits for the deploy of that commit's dashboard")
    fi
    if [ -f "$rights_copy" ]; then
        rights="apply the database rights the last apply used again, from ${rights_copy#"$home"/} (only a dashboard deploy moves them forward), after a rehearsal on a throwaway login, and compare them"
    else
        rights="apply the database rights in dashboard-rights.sql, after a rehearsal on a throwaway login, and compare them"
    fi
    if [ "$hosts" != 2 ]; then
        lines+=("create the dashboard's database login, $dashboard_login, on localhost and 127.0.0.1, with a new password" "$rights")
    elif [ "$pw_changes" = yes ]; then
        lines+=("$rights" "then give the dashboard's database login a new password")
    else
        lines+=("$rights")
    fi
    if [ "$key_changes" = yes ] && [ "$container_exists" = yes ]; then
        lines+=("stop the dashboard first, so visitors see nginx's restarting page while the proxy key changes")
    fi
    if [ "$nginx_changes" = yes ]; then
        lines+=("test nginx's files on their own and with every site, and reload nginx for every site on this server only if both tests pass")
    elif [ "$reload_only" = yes ]; then
        lines+=("reload nginx, which serves an older copy of the certificate")
    fi
    if [ ${#changed[@]} -gt 0 ] && [ "$container_exists" = yes ]; then
        lines+=("recreate the dashboard with the new settings: about 10 seconds of the restarting page")
    elif [ "$running" = false ]; then
        lines+=("start the dashboard, which isn't running: its container is stopped, so visitors get nginx's restarting page until then")
    elif [ "$running" = true ] && [ ${#changed[@]} -eq 0 ] && container_env_stale; then
        lines+=("recreate the dashboard, which runs on other settings than dashboard.env holds: a recreate failed, or a run was cut off after dashboard.env moved")
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

# What die does first in the unit, when dashboard.env didn't move: it leaves dashboard.env agreeing with nginx and
# MariaDB, and the dashboard running on it.
# - nginx runs the new files already, with their proxy key: the candidate moves, whole, and the dashboard is recreated.
# - Else the database password changed in this run, which can't be taken back: dashboard.env gets the new password and
#   nothing else, and the dashboard is recreated.
# - Else a dashboard this run stopped starts again on its old settings.
# It also says so when an nginx change is left cut off.
before_stop() {
    [ "${run_mode:-}" = detached ] || return 0
    if [ "${env_moved:-no}" = no ]; then
        if [[ ${nginx_result:-} =~ ^(reloaded|problem)$ ]] && [ -f "$home/dashboard.env.next" ]; then
            env_moved=yes
            if mv -f "$home/dashboard.env.next" "$env_file"; then
                echo "nginx runs the new files already, so dashboard.env got its new settings too."
                recreate_after_stop "with its new settings"
            else
                echo "nginx runs the new files already, but dashboard.env couldn't take its new settings (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
            fi
        elif [ "${pw_applied:-no}" = yes ] && [ -f "$env_file" ]; then
            password_to_env
        elif [ "${stopped_dashboard:-no}" = yes ]; then
            echo "Starting the dashboard again on its old settings."
            if start_again; then
                echo "The dashboard runs again on its old settings."
            else
                echo "The dashboard didn't get ready again (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
            fi
        fi
    fi
    rm -f "$home/dashboard.env.next"
    if [ -e "$home/state/nginx-change" ]; then
        echo "An nginx change was left cut off. Run ./setup-dashboard.sh again: it undoes that first."
    fi
}

# The database password changed in this run and dashboard.env didn't move: dashboard.env gets the new password and
# nothing else, since the old one no longer works, and the dashboard is recreated with it
password_to_env() {
    env_moved=yes
    write_env "$env_file" ConnectionStrings__DefaultConnection "$(connection_string "$SITE_DB" "$pw")"
    write_env "$env_file" ConnectionStrings__BotConnection "$(connection_string "$BOT_DB" "$pw")"
    echo "The dashboard's database password changed in this run, so dashboard.env got the new one and nothing else."
    recreate_after_stop "with its old settings and the new password"
}

# Starts the dashboard this run stopped again, on its old settings, and waits until it's ready. Its restart count is
# taken once Compose is done: a container it started begins again at 0, and one it left running, as after a stop call
# that failed before Docker stopped it, keeps its earlier restarts, which aren't this run's.
start_again() {
    local base out status=0
    start_service dashboard || return 1
    # Looked at the way restart_count does in deploy.sh. Docker not answering is never a count of 0, which would make
    # restarts from before this run look like new ones: the readiness check then counts from its own first look.
    out=$(run_for 30 docker container inspect -f '{{.RestartCount}}' "$dashboard_container" 2>&1) || status=$?
    if [ "$status" = 0 ] && [[ $out =~ ^[0-9]+$ ]]; then
        base=$out
    elif [[ $out == *"No such container"* || $out == *"No such object"* ]]; then
        base=0
    else
        printf '%s\n' "$out"
        echo "Docker didn't answer about the dashboard's restart count (above), so the readiness check counts restarts from its own first look."
        base=first
    fi
    ready_dashboard "" "$base" || return 1
    note_ready dashboard
}

# The dashboard container's ID, or nothing when there's no such container. Docker not answering is never "no container":
# Docker's own words go to stderr and it returns 1, which its callers handle, since they run where set -e is off.
dashboard_id() {
    local out status=0
    out=$(run_for 30 docker container inspect -f '{{.Id}}' "$dashboard_container" 2>&1) || status=$?
    if [ "$status" = 0 ]; then
        printf '%s\n' "$out"
        return 0
    fi
    case $out in
        *"No such container"* | *"No such object"*) return 0 ;;
    esac
    printf '%s\n' "$out" >&2
    return 1
}

# Recreates the dashboard on dashboard.env as it is now, and waits until it's ready. That counts only when Compose exits
# 0 and the container is a new one: a recreate that failed can leave the old container running on its old settings, and
# ready all the same. Once the dashboard is ready, the compose.yml it runs under is kept as the one it was last ready
# under (note_ready), which a failed deploy swap puts back.
recreate_dashboard() {
    local before after since started
    since=$(date -u +%Y-%m-%dT%H:%M:%S)
    # Without the ID from before, as when Docker didn't answer that look, the recreate still runs, and the container
    # counts as new only when it started since then
    if ! before=$(dashboard_id); then
        before=unknown
        echo "Docker didn't answer about the dashboard's container (above), so the recreate counts only a container started since $since."
    fi
    start_service dashboard --force-recreate || return 1
    if ! after=$(dashboard_id); then
        echo "Docker didn't answer about the dashboard's container (above), so whether Compose recreated it can't be told."
        return 1
    fi
    if [ "$before" = unknown ] && [ -n "$after" ]; then
        started=$(run_for 30 docker container inspect -f '{{.State.StartedAt}}' "$dashboard_container" 2> /dev/null) \
            || started=
        if ! [[ ${started:0:19} =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$ ]] || [[ ${started:0:19} < $since ]]; then
            after=$before
        fi
    fi
    if [ -z "$after" ] || [ "$after" = "$before" ]; then
        echo "The dashboard isn't ready: Compose didn't make it a new container, so it doesn't run on dashboard.env as it is now."
        return 1
    fi
    ready_dashboard || return 1
    note_ready dashboard
}

# Recreates the dashboard after a stop, when its container exists. It looks the container up itself: a stop can come
# before step 7 does.
recreate_after_stop() {   # recreate_after_stop <how it runs, for the message>
    local id
    if ! id=$(container_image "$dashboard_container"); then
        echo "Docker didn't answer whether the dashboard's container exists (above), so it wasn't recreated. Run ./setup-dashboard.sh --no-questions once Docker answers."
        return 0
    fi
    [ -n "$id" ] || return 0
    if recreate_dashboard; then
        echo "The dashboard runs again, $1."
    else
        echo "The dashboard didn't get ready (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
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

# Creates the login on the missing hosts given, and gives every other host the same new password: with no host given,
# both get it. The statements go in on stdin, never on a command line, and with --skip-print-query-on-error, without
# which MariaDB's client prints a failing statement, password included.
set_password() {   # set_password [<missing host>...]
    local host sql=''
    step="creating or changing the dashboard's database login"
    for host in localhost 127.0.0.1; do
        if [[ " $* " == *" $host "* ]]; then
            sql+="CREATE USER '$dashboard_login'@'$host' IDENTIFIED BY '$pw';"$'\n'
        else
            sql+="ALTER USER '$dashboard_login'@'$host' IDENTIFIED BY '$pw';"$'\n'
        fi
    done
    run_for 30 mariadb --batch --skip-print-query-on-error <<< "$sql" \
        || die "Creating the dashboard's database login or changing its password failed (above). Run ./setup-dashboard.sh again to finish: it makes the rest agree."
    pw_applied=yes
}

# Applies the rights file the last apply used, from the copy apply_rights keeps; before any apply has kept one, the
# installed dashboard-rights.sql. It says which.
apply_login_rights() {
    local file=$home/dashboard-rights.sql
    step="applying the dashboard's database rights"
    if [ -f "$rights_copy" ]; then
        file=$rights_copy
        echo "Applying the rights the last apply used, from ${rights_copy#"$home"/}: only a dashboard deploy moves them forward."
    else
        echo "Applying the rights in dashboard-rights.sql: no apply has kept a copy of its file yet."
    fi
    if ! apply_rights "$file"; then
        if [ "$pw_applied" = yes ]; then
            die "The dashboard's database rights didn't apply (above). Its login was created in this run, before the rights, which need it on both hosts. Once the cause is fixed, run ./setup-dashboard.sh again: it finishes the rest."
        fi
        die "The dashboard's database rights didn't apply (above), and its password is as it was. Once the cause is fixed, run ./setup-dashboard.sh again."
    fi
}

# nginx refused the new files and runs the old ones, which match the dashboard.env that didn't move. A dashboard this run
# stopped starts again on it. A database password this run changed can't be taken back, so then dashboard.env gets only
# the new password, and the dashboard is recreated with it. When nginx took no reload of the old files either,
# nginx_put_back keeps the marker and says so (above): nginx may then still run what a reload that failed loaded, so the
# message doesn't say that nothing changed, and the next run reloads the old files first.
nginx_refused() {
    local said="nginx refused the new files, so nothing changed" kept="nginx refused the new files, so nginx and the dashboard's other settings are as they were."
    if [ -e "$home/state/nginx-change" ]; then
        said="nginx refused the new files, and the old ones are back on disk, though nginx hasn't loaded them again (above)"
        kept="$said. The dashboard's other settings are as they were."
    fi
    rm -f "$home/dashboard.env.next"
    if [ "$pw_applied" = yes ] && [ -f "$env_file" ]; then
        password_to_env
        die "$kept"
    fi
    env_moved=yes
    if [ "$stopped_dashboard" = yes ]; then
        if start_again; then
            die "$said, and the dashboard runs again on its old settings."
        fi
        die "$said, but the dashboard this run stopped didn't get ready again (above). Run ./setup-dashboard.sh --no-questions once the cause is fixed."
    fi
    die "$said."
}

not_ready_after_recreate() {
    local tmp
    if [ "$pw_changed" = no ] && [ "$key_changed" = no ] && [ -f "$home/dashboard.env.previous" ]; then
        echo "The dashboard wasn't ready with the new settings, so the earlier ones go back."
        tmp=$(mktemp "$home/.dashboard.env.XXXXXX")
        cp -p "$home/dashboard.env.previous" "$tmp"
        mv -f "$tmp" "$env_file"
        if recreate_dashboard; then
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
    local cur_key sent_key cur_pw host n image id served expected ends name joined running
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

    # 1. A change that was cut off is undone before anything else, so a stop before step 6 names that undo
    if [ -e "$home/state/nginx-change" ]; then
        step="undoing a cut-off nginx change"
        nginx_undo_cut_off
        case $nginx_result in
            undone)
                changes+=("cut-off nginx change undone")
                unchanged="The cut-off nginx change was undone (above), and nothing else was changed."
                ;;
            none) ;;
            *) die "The nginx change that was cut off couldn't be undone cleanly (above), so nothing else was changed." ;;
        esac
    fi

    step="loading server.conf"
    load_server_conf

    # 2. The proxy key: dashboard.env's, nginx's when that's newer (nginx_proxy_key), or a new one
    step="choosing the proxy key"
    cur_key=$(current Dashboard__ProxyKey)
    sent_key=$(nginx_proxy_key)
    if [ "$rotate_key" = no ] && [[ $cur_key =~ ^[0-9a-f]{64}$ ]] && [ -n "$sent_key" ] && [ "$sent_key" != "$cur_key" ]; then
        key=$sent_key
        key_changed=yes
        echo "nginx sends a newer proxy key than dashboard.env has: a run was cut off after nginx's change, before dashboard.env moved. dashboard.env takes nginx's key, so the old one never goes back."
    elif [ "$rotate_key" = no ] && [[ $cur_key =~ ^[0-9a-f]{64}$ ]]; then
        key=$cur_key
        key_changed=no
    else
        key=$(run_for 10 openssl rand -hex 32) || die "openssl couldn't make a proxy key (above). $unchanged"
        [[ $key =~ ^[0-9a-f]{64}$ ]] || die "openssl made a proxy key of the wrong shape. $unchanged"
        key_changed=yes
        echo "A new proxy key was made. It's never shown."
    fi

    # 3. The database password
    step="checking the dashboard's database password"
    for host in localhost 127.0.0.1; do
        n=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM mysql.user WHERE User = '$dashboard_login' AND Host = '$host'") \
            || die "MariaDB didn't answer (above). $unchanged"
        if [ "$n" != 1 ]; then
            missing_hosts+=("$host")
        fi
    done
    cur_pw=$(current_password)
    # yes once MariaDB has the new password, which before_stop then puts into dashboard.env whatever stops the run
    pw_applied=no
    if [ ${#missing_hosts[@]} -eq 0 ] && [ "$rotate_pw" = no ] && [ -n "$cur_pw" ] && password_works "$cur_pw" "$BOT_DB"; then
        pw=$cur_pw
        pw_changed=no
        echo "The dashboard's database password works, so it stays."
    else
        pw=$(new_password) || die "openssl couldn't make a password (above). $unchanged"
        pw_changed=yes
        echo "The dashboard's database login gets a new password. It's never shown."
    fi

    # 4. The candidate dashboard.env
    step="writing the candidate dashboard.env"
    candidate_env "$key" "$pw" "$home/dashboard.env.next"

    # 5. The dashboard's own validator, before the database or nginx is touched
    step="checking the new settings"
    image=minerbot-dashboard:current
    # Looked up first, so Docker not answering stops the run here: inside [ ], it would read as no image
    id=$(image_id "$image")
    if [ -z "$id" ]; then
        image=minerbot-dashboard:new
    fi
    check_settings "$home/dashboard.env.next" "$image" \
        || die "The dashboard's own check refused the new settings (above). $unchanged"

    # 6. The login, its rights, and its password on both databases. A password change can't be taken back, so when both
    # hosts exist the rights come first, and a rights failure stops the run with the login's password as it was. A
    # missing host must exist before the rights, which name both: then the login is made, and the other host gets the
    # same new password, in one go first.
    if [ ${#missing_hosts[@]} -gt 0 ]; then
        set_password "${missing_hosts[@]}"
        changes+=("login created")
        apply_login_rights
    else
        apply_login_rights
        if [ "$pw_changed" = yes ]; then
            set_password
            changes+=("new database password")
        fi
    fi
    step="testing the dashboard's password on both databases"
    if ! password_works "$pw" "$SITE_DB" || ! password_works "$pw" "$BOT_DB"; then
        die "The dashboard's login can't open both databases with its password (above)."
    fi
    echo "The dashboard's login opens both databases with its password."

    # 7. What else needs to change
    step="comparing nginx's files"
    install -d -m 700 "$work/nginx"
    nginx_conf_source
    if [ -n "$conf_note" ]; then
        echo "$conf_note"
    fi
    cp "$conf_source" "$work/nginx/minerbot-dashboard.conf"
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
    if pipelog_stale; then
        nginx_files_change=yes
    fi
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
    # true or false for a container that exists, nothing when there's none
    running=$(container_running "$dashboard_container")
    container_exists=no
    if [ -n "$running" ]; then
        container_exists=yes
    fi
    env_stale=no
    if [ "$env_changed" = no ] && [ "$running" = true ] && container_env_stale; then
        env_stale=yes
    fi

    # 8. In this order. Each cut_point is a test seam that does nothing in a real run.
    stopped_dashboard=no
    env_moved=no
    if [ "$key_changed" = yes ] && [ "$container_exists" = yes ]; then
        step="stopping the dashboard"
        echo "Stopping the dashboard while the proxy key changes, so visitors see nginx's restarting page."
        # Set first: a stop call that fails or hits its limit may have stopped the container all the same, and before_stop
        # starts it again only when this says so
        stopped_dashboard=yes
        compose 60 stop dashboard
        cut_point 8-stop
    fi
    if [ "$nginx_files_change" = yes ]; then
        step="changing nginx's files"
        nginx_change "$work/nginx"
        case $nginx_result in
            refused) nginx_refused ;;
            problem) problems+=("$nginx_problem") ;;
        esac
        changes+=("nginx files")
        cut_point 8-nginx
    elif [ "$reload_only" = yes ]; then
        step="reloading nginx"
        # cpanel_reload counts the reload only once nginx took it, as nginx_change's does
        if run_for 30 nginx -t -q && cpanel_reload; then
            echo "nginx reloaded: it served an older copy of the certificate."
            changes+=("nginx reloaded")
        else
            problems+=("nginx serves another certificate than the installed one, and the reload that would fix it failed, or nginx didn't take it (above).")
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
    cut_point 8-env
    if [ "$env_changed" = yes ] && [ "$container_exists" = yes ]; then
        step="recreating the dashboard"
        echo "Recreating the dashboard with its new settings."
        if ! recreate_dashboard; then
            not_ready_after_recreate
        fi
        changes+=("dashboard recreated")
    elif [ "$container_exists" = yes ] && { [ "$running" != true ] || [ "$stopped_dashboard" = yes ]; }; then
        # A container that exists but doesn't run: a run of this script cut off after it stopped it, a stop call that
        # failed after Docker had stopped it, a start that failed, or a stop by hand. Docker's restart policy never starts
        # a container stopped that way again, not even at boot, so every run starts it. It's recreated, so it runs on
        # dashboard.env as it is now, also when a cut-off run moved dashboard.env after it stopped the container.
        step="starting the dashboard"
        echo "Starting the dashboard, which isn't running."
        recreate_dashboard \
            || die "The dashboard didn't get ready after it was started (above). Its log: docker logs --tail 50 $dashboard_container. Once the cause is fixed, run ./setup-dashboard.sh --no-questions, which starts it again if it's stopped."
        changes+=("dashboard started")
    elif [ "$env_stale" = yes ]; then
        step="recreating the dashboard"
        echo "Recreating the dashboard, which runs on other settings than dashboard.env holds: a recreate failed, or a run was cut off after dashboard.env moved."
        recreate_dashboard \
            || die "The dashboard didn't get ready on dashboard.env's settings (above). Its log: docker logs --tail 50 $dashboard_container. Once the cause is fixed, run ./setup-dashboard.sh --no-questions again."
        changes+=("dashboard recreated")
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

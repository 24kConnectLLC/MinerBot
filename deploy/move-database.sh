#!/usr/bin/env bash
# Moves MinerBot's own database out of the cPanel account that owns it, once, with the owner's go-ahead. A cPanel account
# can take over every database and login its map lists, and so can anything that runs as that account, such as its
# websites' code: it could read the audit log, with the addresses people signed in from, and change or delete what the
# dashboard writes. The new database and the bot's login there are root's alone, under names cPanel can never claim,
# moved_db and bot_login in common.sh. deploy/README.md has the whole story.
#
# Nothing is copied from the old database: it's the cPanel account's, so its tables, views and names may be that
# account's doing, and root would run them. The new database gets its tables from the migrations of the dashboard that
# runs, and starts empty: the old rows stay in the pre-move dump, and whoever was signed in signs in again.
#
# Usage, as root, from the checkout deploy.sh keeps, once a deploy has installed the same commit's deploy files:
#   bash /opt/minerbot/src/deploy/move-database.sh
#       Dumps the old database, makes the new one and the bot's login there, runs the migrations into it, points
#       dashboard.env, bot.env and server.conf at it, moves the dashboard's rights there, and recreates the dashboard,
#       which must be ready. The old database stays as it was. A failure before the dashboard runs on the new database
#       puts everything back on the old one; after, the next run finishes the move.
#   bash /opt/minerbot/src/deploy/move-database.sh --drop-old
#       After the move, once the dashboard works: deletes the old database with cPanel's own tool, as the account that
#       owns it, so cPanel's map stays right.
# Each runs as the systemd unit minerbot-move-database, for 15 minutes at most, and this terminal follows its log. A run
# cut off anywhere is finished by running it again. The bot keeps running: it doesn't use MinerBot's database yet. Only
# the deploy's migrations take bot.env's ConnectionStrings__BotConnection, and the bot gets it when it's next recreated.
# Exit code: 0 done; 1 refused or stopped, with the reason.
set -Eeuo pipefail
umask 077
# shellcheck source=common.sh
source "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/common.sh"

action=move-database
subject=database
step="taking its options"
trap 'stopped $? $LINENO' ERR
usage="Usage: bash $home/src/deploy/move-database.sh [--drop-old]"
self=$(readlink -f "${BASH_SOURCE[0]}")
# While a move runs, and after one stopped: the old database's name, and yes when the dashboard had a container when the
# move began. It shows that the new database and login are this script's own, so a run that starts over may remake them.
marker=$home/state/database-move
# The values the move changes, as they were: server.conf's BOT_DB and bot.env's ConnectionStrings__BotConnection, one per
# line (dashboard.env's, a third line, only for the record: its database's name is all that goes back). A failure puts
# back these values only, never whole files, so a change made since, by setup or fill-secrets, stays.
values=$home/state/database-move-values
# Written right before the dashboard first runs on the new database. From then on, what it writes is there only, so the
# move never goes back, and a run that starts over never drops the new database.
ran=$home/state/database-move-ran
# After a move: when, from which database and to which, and once the old one is gone, when it went
moved=$home/state/database-moved
phase=none
old=
had=no
target=$moved_db

apply=no
mode=move
while [ $# -gt 0 ]; do
    case $1 in
        --apply) apply=yes ;;
        --drop-old) mode=drop-old ;;
        *) die "$usage" ;;
    esac
    shift
done

first_line() {   # first_line <file>: its first line, or nothing
    local line=''
    if [ -f "$1" ]; then
        IFS= read -r -t 5 line < "$1" || true
    fi
    printf '%s\n' "$line"
}

# Writes a file of the state folder by rename, 600, so a run cut off never leaves half of it
state_file() {   # state_file <file> <text>
    local tmp
    install -d -m 700 "$home/state"
    tmp=$(mktemp "$1.XXXXXX")
    printf '%s\n' "$2" > "$tmp"
    chmod 600 "$tmp"
    mv -f "$tmp" "$1"
}

# ---- The foreground part: it changes nothing

foreground_part() {
    local installed checked_out done_line
    step="checking before it starts"
    [ "$(id -u)" = 0 ] || die "Run this as root. Nothing was changed."
    cd "$home"
    [ "$self" = "$home/src/deploy/move-database.sh" ] || die "Run it from the checkout deploy.sh keeps. $usage Nothing was changed."
    # backup.sh and the rights come from the installed deploy files, and this script and common.sh from src: both must be
    # the same commit, or the two could disagree
    installed=$(first_line "$home/state/deploy-files.commit")
    checked_out=$(run_for 30 git -C "$home/src" rev-parse HEAD 2> /dev/null) || checked_out=
    if [ -z "$installed" ] || [ "$installed" != "$checked_out" ]; then
        die "The installed deploy files are from ${installed:0:12}, and src is at ${checked_out:0:12}. Deploy first, with ./deploy.sh main --only dashboard, so both are the commit this script comes from. Nothing was changed."
    fi
    load_server_conf
    done_line=$(first_line "$moved")
    if [ "$mode" = move ] && [ -n "$done_line" ]; then
        die "MinerBot's database was moved already ($done_line). Nothing was changed."
    fi
    if [ "$mode" = drop-old ]; then
        [ -n "$done_line" ] || die "No move has finished on this server, so there's no old database to delete. Nothing was changed."
        if [[ $done_line == *" dropped "* ]]; then
            die "The old database was deleted already ($done_line). Nothing was changed."
        fi
    fi
    lock_is_free || die "Another run holds the lock: $(lock_holder). Wait for it to finish, then try again. Nothing was changed."
    if [ "$mode" = drop-old ]; then
        echo "Deleting the old MinerBot database through cPanel."
        run_detached move-database 900 "$self" --apply --drop-old && exit 0
    else
        echo "Moving MinerBot's database out of cPanel."
        run_detached move-database 900 "$self" --apply && exit 0
    fi
    exit 1
}

# ---- The move: the unit minerbot-move-database

# The checks before anything changes, also for a run that starts a move over. Sets account, bot_cs and dash_cs.
check_before_move() {
    local n
    step="checking before the move"
    account=$(cpanel_owner "$old")
    [ -n "$account" ] \
        || die "MinerBot's database, $old, isn't in any cPanel account's map, so it isn't a cPanel database, and there's nothing to move. Nothing was changed."
    [ -n "$(image_id minerbot-dashboard:current)" ] \
        || die "There's no minerbot-dashboard:current, whose migrations make the new database's tables. Deploy the dashboard first. Nothing was changed."
    bot_cs=$(env_value "$home/bot.env" ConnectionStrings__BotConnection) \
        || die "bot.env's ConnectionStrings__BotConnection can't be used as it is (above). Nothing was changed."
    [[ ";$bot_cs;" == *";Database=$old;"* ]] \
        || die "bot.env's ConnectionStrings__BotConnection doesn't name $old as its Database=, which server.conf's BOT_DB does. Make them agree first. Nothing was changed."
    with_parts "$bot_cs" "Database=$target" "Uid=$bot_login" "Pwd=x" > /dev/null \
        || die "bot.env's ConnectionStrings__BotConnection hasn't exactly one each of Database=, Uid= and Pwd=, so its login can't be changed. Nothing was changed."
    dash_cs=
    if [ -f "$home/dashboard.env" ]; then
        dash_cs=$(env_value "$home/dashboard.env" ConnectionStrings__BotConnection) \
            || die "dashboard.env's ConnectionStrings__BotConnection can't be used as it is (above). Nothing was changed."
    fi
    if [ -n "$dash_cs" ]; then
        if [[ ";$dash_cs;" != *";Database=$old;"* ]] || ! with_parts "$dash_cs" "Database=$target" > /dev/null; then
            die "dashboard.env's ConnectionStrings__BotConnection doesn't name $old once as its Database=, which server.conf's BOT_DB does. Run ./setup-dashboard.sh --no-questions first. Nothing was changed."
        fi
    fi
    n=$(database_exists "$target") || die "MariaDB didn't answer (above). Nothing was changed."
    if [ "$n" != 0 ] && [ -z "$was_old" ]; then
        die "A database named $target is on this server already, and this script didn't make it. Find out whose it is. Nothing was changed."
    fi
    n=$(run_for 30 mariadb -N -e "SELECT COUNT(*) FROM mysql.user WHERE User = '$bot_login'") \
        || die "MariaDB didn't answer (above). Nothing was changed."
    if [ "$n" != 0 ] && [ -z "$was_old" ]; then
        die "A login named $bot_login is on this server already, and this script didn't make it. Find out whose it is. Nothing was changed."
    fi
}

# The rights file the last apply used, as setup-dashboard.sh applies it; before any apply kept one, the installed file
rights_file() {
    if [ -f "$home/state/dashboard-rights.applied.sql" ]; then
        printf '%s\n' "$home/state/dashboard-rights.applied.sql"
    else
        printf '%s\n' "$home/dashboard-rights.sql"
    fi
}

# Puts back the values the move changes, server.conf's first, each by write_env, which keeps every other line, and checks
# each one; then the dashboard's rights on the old database. dashboard.env gets back only its database's name, so a
# password setup gave the dashboard's login since stays. It returns 1 at the first that doesn't hold.
put_back_values() {
    local conf_old='' bot_old='' dash_now dash_back
    [ -f "$values" ] || return 1
    { IFS= read -r -t 5 conf_old && IFS= read -r -t 5 bot_old; } < "$values" || true
    [ "$conf_old" = "$old" ] && [ -n "$bot_old" ] || return 1
    write_env "$home/server.conf" BOT_DB "$conf_old" && [ "$(conf_value BOT_DB)" = "$conf_old" ] || return 1
    write_env "$home/bot.env" ConnectionStrings__BotConnection "$bot_old" \
        && [ "$(env_value "$home/bot.env" ConnectionStrings__BotConnection)" = "$bot_old" ] || return 1
    dash_now=$(env_value "$home/dashboard.env" ConnectionStrings__BotConnection 2> /dev/null) || return 1
    if [[ ";$dash_now;" == *";Database=$target;"* ]]; then
        dash_back=$(with_parts "$dash_now" "Database=$old") || return 1
        write_env "$home/dashboard.env" ConnectionStrings__BotConnection "$dash_back" \
            && [ "$(env_value "$home/dashboard.env" ConnectionStrings__BotConnection)" = "$dash_back" ] || return 1
    fi
    BOT_DB=$old
    apply_rights "$(rights_file)" || return 1
    rm -f "$values"
}

# A failure goes back as far as is safe, once. Before the dashboard runs on the new database, the three values and the
# dashboard's rights go back to the old one, where the dashboard still runs: it isn't recreated before then. After, what
# it wrote is in the new database only, so everything stays there, and the next run finishes the move. It never calls die.
before_stop() {
    case $phase in
        switched)
            echo "Putting server.conf, bot.env and dashboard.env back on $old, with the dashboard's rights there."
            if put_back_values; then
                echo "They name $old again, and the dashboard, which never left it, has its rights there. The next run of this script starts the move over."
            else
                echo "They couldn't all be put back (above): ${values#"$home"/} holds the values as they were. Run this again once the cause is fixed: it puts them back first."
            fi
            ;;
        ran)
            echo "The dashboard has run on $target, so the move doesn't go back: what it wrote is there only. Run this again once the cause is fixed: it finishes the move."
            ;;
    esac
}

# From the rights on: the part a run that stopped after server.conf moved finishes too
finish_switch() {
    phase=switched
    if [ -f "$ran" ]; then
        phase=ran
    fi
    BOT_DB=$target
    step="moving the dashboard's rights to $target"
    apply_rights "$(rights_file)" || die "The dashboard's database rights didn't move to $target (above)."
    cut_point rights
    if [ "$had" = yes ]; then
        step="recreating the dashboard on $target"
        state_file "$ran" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        phase=ran
        cut_point ran
        start_service dashboard --force-recreate || die "Compose couldn't recreate the dashboard on $target (above)."
        ready_dashboard || die "The dashboard isn't ready on $target (above)."
        note_ready dashboard
    fi
    cut_point ready
    step="finishing"
    state_file "$moved" "$(date -u +%Y-%m-%dT%H:%M:%SZ) $old $target"
    rm -f "$values" "$ran" "$marker"
    phase=finished
    echo "MinerBot's database is now $target, outside cPanel, with the bot's own login $bot_login there, and the dashboard runs on it. Whoever was signed in signs in again. The old database, $old, stays as it was, and the pre-move dump holds it too. Once you've checked the dashboard, delete it with: bash $home/src/deploy/move-database.sh --drop-old"
    finish ok "moved $old to $target"
}

detached_move() {
    local pw n runs line
    exec >> "$MINERBOT_LOG" 2>&1
    run_mode=detached
    step="taking the lock"
    take_lock move-database
    cd "$home"
    echo "Moving MinerBot's database out of cPanel, as the unit minerbot-move-database, $(date -u +%Y-%m-%dT%H:%M:%SZ)."

    step="loading server.conf"
    load_server_conf
    line=$(first_line "$marker")
    was_old=
    read -r -t 5 was_old had _ <<< "$line" || true
    if [ -n "$line" ] && { ! [[ $was_old =~ ^[A-Za-z0-9_]{1,64}$ ]] || ! [[ $had =~ ^(yes|no)$ ]]; }; then
        die "${marker#"$home"/} isn't as a move leaves it. Look at it before anything else. Nothing more was changed."
    fi
    if [ "$BOT_DB" = "$target" ]; then
        [ -n "$was_old" ] || die "server.conf names $target already, and no move is under way here. Nothing was changed."
        old=$was_old
        echo "An earlier move of $old stopped once server.conf named $target: this run finishes it."
        finish_switch
    fi
    old=$BOT_DB
    if [ -n "$was_old" ]; then
        [ "$was_old" = "$old" ] \
            || die "${marker#"$home"/} names $was_old, but server.conf names $old. Look at both before anything else. Nothing more was changed."
        [ ! -f "$ran" ] \
            || die "The dashboard has run on $target, but server.conf names $old: something changed server.conf since. Look at it before anything else. Nothing more was changed."
        echo "An earlier move of $old stopped before server.conf moved: this run starts it over."
        if [ -f "$values" ]; then
            step="putting back what the earlier run moved"
            put_back_values || die "Couldn't put back what the earlier run moved (above): ${values#"$home"/} holds the values as they were. Nothing more was changed."
            echo "server.conf, bot.env and dashboard.env name $old again, with the dashboard's rights there."
        fi
    fi
    check_before_move
    echo "MinerBot's database, $old, belongs to the cPanel account $account. It moves to $target, with the bot's login $bot_login."
    if [ -z "$was_old" ]; then
        runs=$(container_running "$dashboard_container")
        had=no
        if [ -n "$runs" ]; then
            had=yes
        fi
        state_file "$marker" "$old $had"
    fi
    cut_point marker

    step="dumping $old"
    run_for 660 "$home/backup.sh" pre-move \
        || die "The dump of $old before the move failed (above, and ${backups}/backup.log). Nothing was changed but ${marker#"$home"/}, which the next run reads."
    echo "Dumped $old into $(newest_backup)."
    cut_point dump

    step="making $target"
    n=$(database_exists "$target") || die "MariaDB didn't answer (above). Nothing was moved."
    if [ "$n" != 0 ]; then
        echo "Dropping the $target the earlier run made, which nothing has used, to make it again."
        run_for 60 mariadb -e "DROP DATABASE \`$target\`" || die "Couldn't drop the $target the earlier run made (above). Nothing was moved."
    fi
    run_for 30 mariadb -e "CREATE DATABASE \`$target\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_520_ci" \
        || die "Couldn't make the database $target (above). Nothing was moved."
    cut_point database

    step="making the bot's login"
    pw=$(new_password) || die "openssl couldn't make a password (above). Nothing was moved."
    make_bot_login "$pw" || die "Couldn't make the login $bot_login, or give it its rights (above). Nothing was moved."
    login_works "$bot_login" "$pw" "$target" \
        || die "The new login $bot_login can't open $target with its new password (above). Nothing was moved."
    cut_point login

    step="making $target's tables"
    migrate_with "$(with_parts "$bot_cs" "Database=$target" "Uid=$bot_login" "Pwd=$pw")" minerbot-dashboard:current \
        || die "The migrations of minerbot-dashboard:current didn't make $target's tables (above). Nothing was moved."
    echo "The migrations of the dashboard that runs made $target's tables, with $bot_login."
    cut_point migrate

    step="moving server.conf, bot.env and dashboard.env to $target"
    state_file "$values" "$old"$'\n'"$bot_cs"$'\n'"$dash_cs"
    phase=switched
    cut_point values
    if [ -n "$dash_cs" ]; then
        write_env "$home/dashboard.env" ConnectionStrings__BotConnection "$(with_parts "$dash_cs" "Database=$target")"
    fi
    cut_point dashboard-env
    write_env "$home/bot.env" ConnectionStrings__BotConnection \
        "$(with_parts "$bot_cs" "Database=$target" "Uid=$bot_login" "Pwd=$pw")"
    cut_point bot-env
    write_env "$home/server.conf" BOT_DB "$target"
    echo "server.conf, bot.env and dashboard.env name $target now."
    cut_point conf
    finish_switch
}

# ---- Deleting the old database: the unit minerbot-move-database with --drop-old

# Stops when an env file or the dashboard's container still names the old database
still_named() {
    local name cs vars
    for name in bot.env dashboard.env; do
        cs=$(env_value "$home/$name" ConnectionStrings__BotConnection 2> /dev/null) || cs=
        if [[ ";$cs;" == *";Database=$old;"* ]]; then
            die "$name's ConnectionStrings__BotConnection still names $old, so it wasn't deleted. Make it name $target, as the move left it, then run this again."
        fi
    done
    vars=$(run_for 30 docker container inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$dashboard_container" 2> /dev/null) \
        || vars=
    cs=$(grep -m 1 '^ConnectionStrings__BotConnection=' <<< "$vars") || cs=
    if [[ ";${cs#*=};" == *";Database=$old;"* ]]; then
        die "The dashboard's container still runs on $old, so it wasn't deleted. Recreate it with ./setup-dashboard.sh --no-questions, then run this again."
    fi
}

detached_drop() {
    local done_line when moved_to n out
    exec >> "$MINERBOT_LOG" 2>&1
    run_mode=detached
    step="taking the lock"
    take_lock "move-database --drop-old"
    cd "$home"
    echo "Deleting the old MinerBot database, as the unit minerbot-move-database, $(date -u +%Y-%m-%dT%H:%M:%SZ)."

    step="loading server.conf"
    load_server_conf
    done_line=$(first_line "$moved")
    read -r -t 5 when old moved_to _ <<< "$done_line" || true
    if ! [[ $old =~ ^[A-Za-z0-9_]{1,64}$ ]] || [ "$moved_to" != "$target" ]; then
        die "${moved#"$home"/} isn't as a move leaves it, so nothing was deleted. Look at it before anything else."
    fi
    [ "$BOT_DB" = "$target" ] \
        || die "server.conf's BOT_DB is $BOT_DB, not $target, where the move on $when left it, so nothing was deleted. Find out what changed it."
    still_named

    step="looking at $old"
    n=$(database_exists "$old") || die "MariaDB didn't answer (above), so nothing was deleted. Run this again once it does."
    if [ "$n" != 0 ]; then
        account=$(cpanel_owner "$old")
        [ -n "$account" ] \
            || die "$old isn't in any cPanel account's map, so cPanel's tool can't delete it, and nothing was deleted. Look at it before anything else."
        step="deleting $old"
        echo "Deleting $old with cPanel's own tool, as the account $account, so its map stays right."
        out=$(run_for 120 uapi --output=json --user="$account" Mysql delete_database name="$old" 2>&1) \
            || die "cPanel's uapi failed: $(head -c 400 <<< "$out"). Look in WHM's MySQL Databases whether $old is still there, then run this again."
        if ! run_for 10 jq -e '.result.status == 1' <<< "$out" > /dev/null 2>&1; then
            die "cPanel refused to delete $old: $(run_for 10 jq -r '[.result.errors // [] | .[]] | join("; ")' <<< "$out" 2> /dev/null || head -c 400 <<< "$out"). It's still there. Run this again once the cause is fixed."
        fi
        n=$(database_exists "$old") || die "MariaDB didn't answer (above) after cPanel deleted $old. Run this again: it finishes."
        [ "$n" = 0 ] || die "cPanel said it deleted $old, but it's still there. Look in WHM's MySQL Databases before anything else."
        [ -z "$(cpanel_owner "$old")" ] || die "$old is gone, but cPanel's map still lists it. WHM's Rebuild the MySQL DB Map puts that right."
        echo "$old is gone, and cPanel's map no longer lists it."
    else
        echo "$old isn't on this server any more, so there's nothing to delete."
    fi
    state_file "$moved" "$done_line dropped $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "The bot's cPanel login stays, for the website's database, which belongs to the cPanel account anyway. It must never get a right in $target."
    finish ok "deleted the old database $old"
}

if [ "$apply" = yes ]; then
    if [ "${MINERBOT_DETACHED:-}" != 1 ] || [ -z "${MINERBOT_LOG:-}" ]; then
        die "--apply is only for the run move-database.sh starts itself. Nothing was changed."
    fi
    if [ "$mode" = drop-old ]; then
        detached_drop
    else
        detached_move
    fi
else
    foreground_part
fi

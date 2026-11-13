#!/usr/bin/env bash
# Checks the database rights procedure in deploy/common.sh against a real MariaDB at the server's version, before it
# reaches the server: apply_rights, with render and rights_of, as deploy.sh and setup-dashboard.sh run it. It fills in
# the real deploy/dashboard-rights.sql, rehearses it on a locked throwaway login, applies it to the dashboard's login on
# both its hosts, and compares the two through the real deploy/rights-list.sql. Once that passes, apply_rights keeps a
# copy of the file it applied as state/dashboard-rights.applied.sql, which setup-dashboard.sh applies. It refuses first
# while MinerBot's database holds a trigger, an event or a stored routine, which no migration makes.
#
# MariaDB runs in a throwaway container without a network, with latin1 as the server's default character set and root
# reached through /root/.my.cnf, as on the server. It gets the website's three tables from
# docs/schema/wordpress-tables.sql and MinerBot's first migration, DataAccessLibrary/Migrations/0001_core.sql, under the
# test names website_db and minerbot_db. The checks compare the dashboard's login with a login they give the file's
# rights themselves, without common.sh.
#
# Run it from Git Bash or Linux: bash deploy/tests/rights-test.sh. It needs Docker. It prints one line per check, then
# "N of M passed", and exits 1 if any check failed. Its container is named mbtest-rights-<its PID>, ends by itself after
# 5 minutes, and is removed with its volume when the run ends.
#
# The script runs in three ways: as above, on this machine; with --inside, in the container, where it makes the checks;
# and with --call <function> <arguments>, also in the container, where it runs one of common.sh's functions the way
# deploy.sh runs apply_rights: in a bash of its own, with set -Eeuo pipefail, umask 077 and the scripts' ERR trap.
set -Eeuo pipefail
umask 077

# The server's MariaDB version. Change it with the server's.
image=mariadb:11.4
name=mbtest-rights-$$
checks=21   # how many checks this script makes
made=0
passed=0
notes=""

# The test names the database tests use, and the dashboard's login as common.sh names it
site=website_db
bot=minerbot_db
login=minerbot-dashboard
password=""
expected=""

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

# ---------- In the container: one of common.sh's functions, run as deploy.sh runs apply_rights

call_one() {   # call_one <function> [<arguments>...]
    # shellcheck source=../common.sh
    source /opt/minerbot/common.sh
    trap 'stopped $? $LINENO' ERR
    # What load_server_conf takes from server.conf on the server; MBTEST_BOT_DB stands for a server.conf the move changed
    SITE_DB=$site
    BOT_DB=${MBTEST_BOT_DB:-$bot}
    cd "$home"
    "$@" || die "$1 failed (above)."
}

# ---------- In the container: helpers for the checks

sql() {   # sql <statements>: as root, through /root/.my.cnf; prints MariaDB's answer
    timeout -k 5 30 mariadb --batch -N -e "$1" 2>&1
}

as_login() {   # as_login <database> <statements> [<password>]: the dashboard's login over TCP, its password on stdin
    printf '[client]\nuser=%s\npassword=%s\nhost=127.0.0.1\nprotocol=tcp\n' "$login" "${3:-$password}" \
        | timeout -k 5 15 mariadb --defaults-file=/dev/stdin -D "$1" -N -e "$2" 2>&1
}

rights() {   # rights <user> <host>: that login's rights through rights-list.sql, filled in here with sed, sorted
    sed "s/{{WHO}}/''$1''@''$2''/g" /opt/minerbot/rights-list.sql \
        | timeout -k 5 30 mariadb --batch -N 2>&1 | LC_ALL=C sort
}

login_rights() {   # the dashboard's login's rights on each of its two hosts
    printf '%s:\n%s\n' localhost "$(rights "$login" localhost)" 127.0.0.1 "$(rights "$login" 127.0.0.1)"
}

differs() {   # differs <expected> <found>: the difference, for a problem's details
    diff <(printf '%s\n' "$1") <(printf '%s\n' "$2") || true
}

throwaway_count() {   # how many hosts the rehearsal's throwaway login has
    sql "SELECT COUNT(*) FROM mysql.user WHERE User = 'minerbot-rights-check'"
}

user_statements() {   # how many CREATE USER, DROP USER, ALTER USER, GRANT and REVOKE statements MariaDB has run
    sql "SELECT SUM(VARIABLE_VALUE) FROM information_schema.GLOBAL_STATUS WHERE VARIABLE_NAME IN
        ('COM_CREATE_USER', 'COM_DROP_USER', 'COM_ALTER_USER', 'COM_GRANT', 'COM_REVOKE', 'COM_REVOKE_ALL')"
}

run_common() {   # run_common <function> [<arguments>...]: sets said (what it printed) and status (its exit code)
    status=0
    said=$(timeout -k 10 200 bash "$0" --call "$@" 2>&1) || status=$?
}

# ---------- In the container: the checks, in the order they run

first_apply() {
    local now
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    [ "$status" = 0 ] || problem "apply_rights failed (exit code $status)" "$said"
    expect "rehearsed, applied and compared" "$said" "apply_rights' message"
    now=$(login_rights)
    [ "$now" = "$expected" ] \
        || problem "the login doesn't have exactly the rights the file gives a new login" "$(differs "$expected" "$now")"
}

throwaway_gone() {
    local n
    n=$(throwaway_count)
    [ "$n" = 0 ] || problem "the throwaway login minerbot-rights-check is still there" "$n"
}

connects() {
    local database out
    for database in "$site" "$bot"; do
        out=$(as_login "$database" 'SELECT 1')
        [ "$out" = 1 ] || problem "the login couldn't open $database" "$out"
    done
}

sessions_work() {
    local out
    out=$(as_login "$bot" "
        INSERT INTO $bot.dashboard_sessions (id_hash, user_id, user_name, created_at, last_seen_at, expires_at, ticket)
            VALUES (UNHEX(SHA2('k', 256)), 1, 'n', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3),
                UTC_TIMESTAMP(3) + INTERVAL 1 HOUR, 'x');
        UPDATE $bot.dashboard_sessions SET last_seen_at = UTC_TIMESTAMP(3);
        SELECT COUNT(*) FROM $bot.dashboard_sessions;
        DELETE FROM $bot.dashboard_sessions WHERE user_id = 1 AND id_hash NOT IN (SELECT id_hash FROM
            (SELECT id_hash FROM $bot.dashboard_sessions WHERE user_id = 1 ORDER BY last_seen_at DESC LIMIT 0)
            AS newest);
        SELECT COUNT(*) FROM $bot.dashboard_sessions;")
    [ "$out" = $'1\n0' ] || problem "adding, changing, reading or deleting a session failed" "$out"
}

audit_adds() {
    local out
    out=$(as_login "$bot" "
        INSERT INTO $bot.audit_log (at, actor_role, action, outcome)
            VALUES (UTC_TIMESTAMP(3), 'owner', 'test.add', 'ok');
        SELECT ROW_COUNT();")
    [ "$out" = 1 ] || problem "adding an audit row failed" "$out"
}

# A trigger made in MinerBot's database by another login with rights there, as the bot's own login could make one: it
# rewrites every audit row the dashboard's login adds, with its maker's rights, and no rights file takes it away. So do
# an event and a stored routine, which no migration makes either. apply_rights refuses before it touches the login,
# naming them, and passes again once they're dropped.
extras_refused() {
    local out before
    out=$(sql "CREATE TRIGGER $bot.mbtest_stamp BEFORE INSERT ON $bot.audit_log FOR EACH ROW SET NEW.actor_name = 'nobody';
        CREATE EVENT $bot.mbtest_sweep ON SCHEDULE EVERY 1 DAY DISABLE DO DELETE FROM $bot.audit_log;
        CREATE PROCEDURE $bot.mbtest_rewrite() UPDATE $bot.audit_log SET detail = 'changed';") \
        || problem "couldn't make the trigger, the event and the procedure" "$out"
    out=$(as_login "$bot" "
        INSERT INTO $bot.audit_log (at, actor_id, actor_name, actor_role, action, outcome)
            VALUES (UTC_TIMESTAMP(3), 42, 'admin-a', 'admin', 'test.stamp', 'ok');") \
        || problem "the dashboard's login couldn't add an audit row" "$out"
    [ "$(sql "SELECT actor_name FROM $bot.audit_log WHERE action = 'test.stamp'")" = nobody ] \
        || problem "the trigger didn't rewrite the dashboard's row, so this check proves nothing"
    before=$(login_rights)
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    [ "$status" != 0 ] || problem "apply_rights passed with a trigger, an event and a procedure in MinerBot's database" "$said"
    expect "holds what no migration makes: event mbtest_sweep, procedure mbtest_rewrite, trigger mbtest_stamp." "$said" \
        "apply_rights' message"
    [ "$(login_rights)" = "$before" ] || problem "apply_rights changed the login's rights though it refused" \
        "$(differs "$before" "$(login_rights)")"
    [ "$(throwaway_count)" = 0 ] || problem "apply_rights made its throwaway login though it refused"
    out=$(sql "DROP TRIGGER $bot.mbtest_stamp; DROP EVENT $bot.mbtest_sweep; DROP PROCEDURE $bot.mbtest_rewrite;") \
        || problem "couldn't drop the trigger, the event and the procedure" "$out"
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    [ "$status" = 0 ] || problem "apply_rights failed once they were dropped (exit code $status)" "$said"
}

forbidden_refused() {
    local statement out
    # The login may read the armor and its review history, so these two try what it may not: deleting history and
    # changing an armor's name
    for statement in "SELECT * FROM $bot.audit_log" "UPDATE $bot.audit_log SET outcome = 'x'" \
        "DELETE FROM $bot.armor_reviews" "UPDATE $site.ihvph_armor_styles SET ArmorName = 'x'" \
        "SELECT armor_channel_id FROM $site.ihvph_discord_servers" \
        "UPDATE $site.ihvph_discord_servers SET armor_channel_id = 1" "CREATE TABLE $bot.rights_check (a INT)" \
        "SELECT * FROM mysql.user"; do
        out=$(as_login "$bot" "$statement")
        case $out in
            *"ERROR 1142"* | *"ERROR 1143"* | *"ERROR 1044"*) ;;
            *) problem "not refused: $statement" "$out" ;;
        esac
    done
}

statement_time() {
    local out
    out=$(as_login "$site" 'SELECT @@max_statement_time')
    [ "$out" = 20.000000 ] || problem "the login's max_statement_time isn't 20" "$out"
}

proxy_caught() {
    local out
    out=$(sql "GRANT SELECT ON $site.ihvph_blacklist TO '$login'@'localhost';
        GRANT ALL ON $bot.* TO '$login'@'127.0.0.1';
        GRANT PROXY ON 'someone'@'localhost' TO '$login'@'localhost';") \
        || problem "couldn't add the rights by hand" "$out"
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    [ "$status" != 0 ] || problem "apply_rights passed while the login holds a PROXY grant added by hand" "$said"
    expect "> proxy someone localhost" "$said" "the difference apply_rights printed"
}

hand_rights_gone() {
    local now
    now=$(login_rights)
    if grep -E 'ihvph_blacklist|^schema ' <<< "$now" > /dev/null; then
        problem "a right added by hand is still there" "$now"
    fi
}

passes_without_proxy() {
    local out now
    out=$(sql "REVOKE PROXY ON 'someone'@'localhost' FROM '$login'@'localhost';") \
        || problem "couldn't remove the PROXY grant" "$out"
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    [ "$status" = 0 ] || problem "apply_rights failed (exit code $status)" "$said"
    now=$(login_rights)
    [ "$now" = "$expected" ] || problem "the login doesn't have exactly the file's rights" "$(differs "$expected" "$now")"
}

missing_table_stops() {
    local out before now
    # shellcheck disable=SC2016 # SQL's backticks
    { cat /opt/minerbot/dashboard-rights.sql; echo 'GRANT INSERT ON `{{BOT_DB}}`.`bot_commands` TO {{GRANTEES}};'; } \
        > /work/rights-missing.sql
    # A right the file would take away, so a REVOKE that reached the real login would show
    out=$(sql "GRANT SELECT ON $site.ihvph_blacklist TO '$login'@'localhost';") || problem "couldn't add a right by hand" "$out"
    before=$(login_rights)
    run_common apply_rights /work/rights-missing.sql
    [ "$status" != 0 ] || problem "apply_rights passed a right on a table that doesn't exist" "$said"
    expect "ERROR 1146" "$said" "MariaDB's error"
    expect "The rehearsal of rights-missing.sql on a throwaway login failed" "$said" "apply_rights' message"
    now=$(login_rights)
    [ "$now" = "$before" ] || problem "the real login's rights changed" "$(differs "$before" "$now")"
    [ "$(throwaway_count)" = 0 ] || problem "the throwaway login is still there"
}

unknown_placeholder() {
    local before after
    # shellcheck disable=SC2016 # SQL's backticks
    { cat /opt/minerbot/dashboard-rights.sql; echo 'GRANT SELECT ON `{{OTHER_DB}}`.`x` TO {{GRANTEES}};'; } \
        > /work/rights-unknown.sql
    before=$(user_statements)
    run_common apply_rights /work/rights-unknown.sql
    after=$(user_statements)
    [ "$status" != 0 ] || problem "apply_rights passed a file with an unknown placeholder" "$said"
    expect "placeholder these scripts don't fill" "$said" "apply_rights' message"
    [ "$after" = "$before" ] || problem "MariaDB ran user or grant statements ($before before, $after after)"
}

twice_the_same() {
    local first second once twice
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    first=$status
    once=$(login_rights)
    run_common apply_rights /opt/minerbot/dashboard-rights.sql
    second=$status
    twice=$(login_rights)
    if [ "$first" != 0 ] || [ "$second" != 0 ]; then
        problem "apply_rights failed (exit codes $first and $second)" "$said"
    fi
    [ "$twice" = "$once" ] || problem "the second apply changed the rights" "$(differs "$once" "$twice")"
    [ "$twice" = "$expected" ] \
        || problem "the login doesn't have exactly the file's rights" "$(differs "$expected" "$twice")"
}

current_user() {
    local out
    out=$(as_login "$bot" 'SELECT CURRENT_USER()')
    [ "$out" = "$login@127.0.0.1" ] || problem "CURRENT_USER() isn't $login@127.0.0.1" "$out"
}

wrong_password() {
    local out
    out=$(as_login "$bot" 'SELECT 1' wrong-password-1)
    expect "ERROR 1045" "$out" "a wrong password"
}

# MariaDB lists one login's rights in an order of its own, which can differ between two logins with the same rights, so
# common.sh's listing must come sorted, or the comparison would find a difference where there is none
rights_sorted() {
    run_common rights_of "$login" localhost
    [ "$status" = 0 ] || problem "rights_of failed (exit code $status)" "$said"
    [ "$said" = "$(rights "$login" localhost)" ] \
        || problem "rights_of's listing isn't the login's rights, sorted" "$(differs "$(rights "$login" localhost)" "$said")"
}

# The file gives no right with the grant option, which rights-list.sql shows as "grantable" (and a role's as "admin"), so
# the login can pass none of its rights on: each GRANT of the file, made out to another login, is refused
no_grant_option() {
    local now statement out tried=0
    now=$(login_rights)
    if grep -E ' (grantable|admin)$' <<< "$now" > /dev/null; then
        problem "the login holds a right with the grant option" "$now"
    fi
    while IFS= read -r statement; do
        tried=$((tried + 1))
        out=$(as_login "$bot" "$statement")
        case $out in
            *"ERROR 1142"* | *"ERROR 1143"* | *"ERROR 1044"*) ;;
            *) problem "the login could pass a right on: $statement" "$out" ;;
        esac
    done < <(sed -n -e "s/{{SITE_DB}}/$site/g" -e "s/{{BOT_DB}}/$bot/g" -e "s/{{GRANTEES}}/'someone'@'localhost'/g" \
        -e '/^GRANT /p' /opt/minerbot/dashboard-rights.sql)
    [ "$tried" -gt 0 ] || problem "the rights file has no GRANT line to try"
}

# The copy setup-dashboard.sh applies is the file the last apply that passed used, byte for byte and root's alone. An
# apply that fails leaves it as it was.
applied_copy_kept() {
    local copy=/opt/minerbot/state/dashboard-rights.applied.sql out
    cmp -s "$copy" /opt/minerbot/dashboard-rights.sql \
        || problem "the copy isn't the file the last apply used" "$(ls -l "$copy" 2>&1)"
    [ "$(stat -c '%a %U' "$copy" 2>&1)" = "600 root" ] || problem "the copy isn't root's alone, 600" "$(ls -l "$copy" 2>&1)"
    { cat /opt/minerbot/dashboard-rights.sql; echo '-- As a newer commit has it'; } > /work/rights-newer.sql
    # A rehearsal that fails, and a comparison that fails
    run_common apply_rights /work/rights-missing.sql
    [ "$status" != 0 ] || problem "apply_rights passed a right on a table that doesn't exist" "$said"
    out=$(sql "GRANT PROXY ON 'someone'@'localhost' TO '$login'@'localhost';") || problem "couldn't add a PROXY grant" "$out"
    run_common apply_rights /work/rights-newer.sql
    [ "$status" != 0 ] || problem "apply_rights passed while the login holds a PROXY grant added by hand" "$said"
    cmp -s "$copy" /opt/minerbot/dashboard-rights.sql || problem "an apply that failed changed the copy"
    out=$(sql "REVOKE PROXY ON 'someone'@'localhost' FROM '$login'@'localhost';") || problem "couldn't remove the PROXY grant" "$out"
    # An apply that passes takes its file's place, and setup's apply of the copy itself passes
    run_common apply_rights /work/rights-newer.sql
    [ "$status" = 0 ] || problem "apply_rights failed (exit code $status)" "$said"
    cmp -s "$copy" /work/rights-newer.sql || problem "the copy isn't the file the last apply that passed used"
    run_common apply_rights "$copy"
    [ "$status" = 0 ] || problem "applying the copy itself failed (exit code $status)" "$said"
    cmp -s "$copy" /work/rights-newer.sql || problem "applying the copy itself changed it"
    [ "$(stat -c '%a %U' "$copy" 2>&1)" = "600 root" ] || problem "the copy isn't root's alone, 600" "$(ls -l "$copy" 2>&1)"
}

# move-database.sh's database steps, with the real common.sh: the new database, and the bot's own login with exactly the
# rights the migrations need there, none anywhere else and none it can pass on, also when it's made again; the first
# migration runs with it; and it can make no view, routine, trigger or event, which root's scripts would run
moved_login() {
    local pw out want got kind
    pw=$(head -c 600 /dev/urandom | tr -dc 'A-Za-z0-9' | cut -c 1-40)
    out=$(sql "CREATE DATABASE minerbot CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_520_ci") \
        || problem "couldn't make the database minerbot" "$out"
    run_common make_bot_login "$pw"
    [ "$status" = 0 ] || problem "make_bot_login failed (exit code $status)" "$said"
    run_common make_bot_login "$pw"
    [ "$status" = 0 ] || problem "make_bot_login run again, as a move that starts over runs it, failed (exit code $status)" "$said"
    run_common login_works minerbot-bot "$pw" minerbot
    [ "$status" = 0 ] || problem "the bot's login can't open minerbot with its password" "$said"
    printf '[client]\nuser=minerbot-bot\npassword=%s\nhost=127.0.0.1\nprotocol=tcp\n' "$pw" > /tmp/bot.cnf
    out=$(timeout -k 5 15 mariadb --defaults-file=/tmp/bot.cnf -N -e "SELECT COUNT(*) FROM $bot.schema_version" 2>&1) || true
    expect "denied" "$out" "the bot's login reading the old database"
    want=$(printf 'schema %s minerbot\n' ALTER CREATE DELETE DROP INDEX INSERT REFERENCES SELECT UPDATE | LC_ALL=C sort)
    for host in localhost 127.0.0.1; do
        got=$(rights minerbot-bot "$host")
        if [ "$(grep '^schema ' <<< "$got")" != "$want" ] || grep -qvE '^(global USAGE|schema .*|limits .*)$' <<< "$got"; then
            problem "the bot's login on $host doesn't have exactly the migrations' rights in minerbot" "$got"
        fi
    done
    out=$(tr -d '\r' < /work/0001_core.sql | timeout -k 5 60 mariadb --defaults-file=/tmp/bot.cnf minerbot 2>&1) \
        || problem "the first migration didn't run with the bot's login" "$out"
    [ "$(sql "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'minerbot' AND TABLE_NAME IN ('audit_log', 'dashboard_sessions')")" = 2 ] \
        || problem "the first migration didn't make the dashboard's tables in minerbot"
    for kind in "CREATE VIEW v AS SELECT 1" "CREATE FUNCTION f() RETURNS INT RETURN 1" \
        "CREATE TRIGGER t BEFORE INSERT ON audit_log FOR EACH ROW SET @x = 1" \
        "CREATE EVENT e ON SCHEDULE EVERY 1 DAY DO SELECT 1"; do
        out=$(timeout -k 5 15 mariadb --defaults-file=/tmp/bot.cnf minerbot -N -e "$kind" 2>&1) || true
        expect "denied" "$out" "the bot's login making ${kind%% *} ${kind#* }"
    done
    rm -f /tmp/bot.cnf
}

# apply_rights with server.conf naming the new database, as the move runs it: the dashboard's login gets the file's rights
# there and keeps none on the old database, and it opens the new one
moved_rights() {
    local now out
    MBTEST_BOT_DB=minerbot run_common apply_rights /opt/minerbot/dashboard-rights.sql
    [ "$status" = 0 ] || problem "apply_rights on minerbot failed (exit code $status)" "$said"
    now=$(login_rights)
    ! grep -q " $bot " <<< "$now" || problem "the dashboard's login kept rights on $bot" "$now"
    if ! grep -q '^table SELECT minerbot dashboard_sessions$' <<< "$now" || ! grep -q '^table INSERT minerbot audit_log$' <<< "$now"; then
        problem "the dashboard's login lacks the file's rights on minerbot" "$now"
    fi
    [ "${now// minerbot / $bot }" = "$expected" ] \
        || problem "on minerbot, the login doesn't have exactly the rights the file gives" "$(differs "$expected" "${now// minerbot / $bot }")"
    out=$(as_login minerbot 'SELECT COUNT(*) >= 0 FROM dashboard_sessions')
    [ "$out" = 1 ] || problem "the dashboard's login can't read sessions in minerbot" "$out"
}

# ---------- In the container: the databases, then the checks

inside_container() {
    local ready="" out before reference
    trap 'stop "The container'\''s setup failed at line $LINENO."' ERR
    [ -n "${MARIADB_ROOT_PASSWORD:-}" ] || stop "The container has no MARIADB_ROOT_PASSWORD."
    for _ in $(seq 120); do
        if timeout -k 5 15 healthcheck.sh --connect --innodb_initialized > /dev/null 2>&1; then
            ready=yes
            break
        fi
        sleep 1
    done
    [ -n "$ready" ] || stop "MariaDB wasn't ready within 2 minutes."
    # root through /root/.my.cnf, as on the server
    printf '[client]\nuser=root\npassword=%s\n' "$MARIADB_ROOT_PASSWORD" > /root/.my.cnf
    out=$(sql "SELECT @@character_set_server") || stop "root can't reach MariaDB through /root/.my.cnf: $out"
    [ "$out" = latin1 ] || stop "MariaDB's default character set is $out, not latin1 as on the server."
    out=$(sql "CREATE DATABASE $site; CREATE DATABASE $bot; CREATE USER 'someone'@'localhost' ACCOUNT LOCK;") \
        || stop "Couldn't make the databases: $out"
    out=$(tr -d '\r' < /work/wordpress-tables.sql | timeout -k 5 60 mariadb "$site" 2>&1) \
        || stop "Couldn't make the website's tables: $out"
    out=$(tr -d '\r' < /work/0001_core.sql | timeout -k 5 60 mariadb "$bot" 2>&1) \
        || stop "Couldn't run MinerBot's first migration: $out"

    # The dashboard's login, made as setup-dashboard.sh makes it: a 40-character password, on stdin
    password=$(head -c 600 /dev/urandom | tr -dc 'A-Za-z0-9' | cut -c 1-40)
    [ ${#password} = 40 ] || stop "The password came out ${#password} characters long, not 40."
    out=$(timeout -k 5 30 mariadb --batch --skip-print-query-on-error 2>&1 <<< "
        CREATE USER '$login'@'localhost' IDENTIFIED BY '$password';
        CREATE USER '$login'@'127.0.0.1' IDENTIFIED BY '$password';") \
        || stop "Couldn't make the dashboard's login: $out"

    # The rights the file gives a new login, applied here with sed and MariaDB's client, without common.sh. The checks
    # compare the dashboard's login with these.
    before=$(user_statements)
    out=$(sql "CREATE USER 'mbtest-reference'@'localhost' ACCOUNT LOCK;") \
        || stop "Couldn't make the reference login: $out"
    out=$(sed -e "s/{{SITE_DB}}/$site/g" -e "s/{{BOT_DB}}/$bot/g" \
        -e "s/{{GRANTEES}}/'mbtest-reference'@'localhost'/g" /opt/minerbot/dashboard-rights.sql \
        | timeout -k 5 60 mariadb --batch 2>&1) \
        || stop "MariaDB refused the rights file for the reference login: $out"
    reference=$(rights mbtest-reference localhost)
    out=$(sql "DROP USER 'mbtest-reference'@'localhost';") || stop "Couldn't drop the reference login: $out"
    grep -q '^table ' <<< "$reference" || stop "The rights file gave a new login no table right: $reference"
    [ "$(user_statements)" -gt "$before" ] \
        || stop "MariaDB's counts of user and grant statements didn't move, so check 12 couldn't see one."
    expected=$(printf '%s:\n%s\n' localhost "$reference" 127.0.0.1 "$reference")
    trap - ERR

    first_apply || true
    verdict "1. The first apply: the rehearsal, the apply and the comparison pass, and the login has exactly the rights the file gives a new login, on both its hosts."
    throwaway_gone || true
    verdict "2. The throwaway login is gone afterwards."
    connects || true
    verdict "3. The login can open the website's database and MinerBot's."
    sessions_work || true
    verdict "4. Sessions can be added, changed, read and deleted, the LIMIT subquery included."
    audit_adds || true
    verdict "5. Audit rows can be added."
    forbidden_refused || true
    verdict "6. Eight forbidden statements are refused."
    statement_time || true
    verdict "7. @@max_statement_time is 20 in the login's sessions."
    proxy_caught || true
    verdict "8. A PROXY grant added by hand, which REVOKE can't take away, is caught by the comparison, which prints it."
    hand_rights_gone || true
    verdict "9. Table and database rights added by hand are taken away."
    passes_without_proxy || true
    verdict "10. Once the PROXY grant is removed by hand, the apply passes again."
    missing_table_stops || true
    verdict "11. A right on a table that doesn't exist stops at the rehearsal (1146), with the real login's rights untouched and the throwaway login removed."
    unknown_placeholder || true
    verdict "12. An unknown placeholder is refused before any SQL."
    twice_the_same || true
    verdict "13. Applying twice changes nothing."
    current_user || true
    verdict "14. CURRENT_USER() is $login@127.0.0.1, with the password read from stdin."
    wrong_password || true
    verdict "15. A wrong password gives 1045."
    rights_sorted || true
    verdict "16. common.sh's rights_of lists the login's rights sorted, so two logins with the same rights compare equal."
    no_grant_option || true
    verdict "17. The login holds no right with the grant option, and can pass none of the file's rights on to another login."
    applied_copy_kept || true
    verdict "18. The last apply that passed leaves a copy of its file, root's alone, which applies again; an apply that fails leaves the copy as it was."
    extras_refused || true
    verdict "19. A trigger that rewrites the dashboard's audit rows, an event and a procedure in MinerBot's database, which no migration makes, stop apply_rights before it touches the login, each named; once they're dropped, it passes."
    moved_login || true
    verdict "20. move-database.sh's steps: the bot's own login has exactly the migrations' rights in minerbot and none elsewhere, also when it's made again; the first migration runs with it; it can make no view, function, trigger or event."
    moved_rights || true
    verdict "21. apply_rights with server.conf naming minerbot gives the dashboard's login exactly the file's rights there and none on the old database."

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
    echo "The container $name is still there. Remove it, and its volume, with: docker rm -f -v $name"
}

copy_in() {   # copy_in <file here> <path in the container>: through docker exec's input, which needs no path conversion
    local out
    # shellcheck disable=SC2016 # the $ are for the container's shell
    out=$(timeout -k 10 60 docker exec -i "$name" sh -c 'mkdir -p "${1%/*}" && cat > "$1"' sh "$2" < "$1" 2>&1) \
        || stop "Couldn't copy $1 into $name: $out"
}

on_host() {
    local here repo file out line status="" root_password
    # Git Bash would otherwise turn the container's paths below into Windows paths
    export MSYS_NO_PATHCONV=1
    here=$(cd "$(dirname "$0")" && pwd)
    repo=$(cd "$here/../.." && pwd)
    trap 'code=$?; cleanup || true; exit "$code"' EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP

    echo "Checking apply_rights in deploy/common.sh, with the real rights files, against $image"
    for file in deploy/common.sh deploy/dashboard-rights.sql deploy/rights-list.sql docs/schema/wordpress-tables.sql \
        DataAccessLibrary/Migrations/0001_core.sql; do
        [ -f "$repo/$file" ] || stop "There's no file at $repo/$file."
    done
    timeout -k 10 30 docker version > /dev/null 2>&1 || stop "Docker isn't running, or doesn't answer."
    if ! timeout -k 10 30 docker image inspect "$image" > /dev/null 2>&1; then
        timeout -k 10 300 docker pull -q "$image" > /dev/null || stop "Couldn't pull $image within 5 minutes."
    fi
    # A made-up root password for this run only. MariaDB itself runs under timeout, so even a run killed before it can
    # clean up leaves nothing running after 5 minutes, and --rm then removes the container and its volume.
    root_password=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
    out=$(MARIADB_ROOT_PASSWORD=$root_password timeout -k 10 120 docker run -d --rm --name "$name" --network none \
        -e MARIADB_ROOT_PASSWORD --entrypoint timeout "$image" -k 30 300 docker-entrypoint.sh mariadbd \
        --character-set-server=latin1 --collation-server=latin1_swedish_ci 2>&1) \
        || stop "Couldn't start the container $name: $out"
    copy_in "$0" /work/rights-test.sh
    copy_in "$repo/deploy/common.sh" /opt/minerbot/common.sh
    copy_in "$repo/deploy/dashboard-rights.sql" /opt/minerbot/dashboard-rights.sql
    copy_in "$repo/deploy/rights-list.sql" /opt/minerbot/rights-list.sql
    copy_in "$repo/docs/schema/wordpress-tables.sql" /work/wordpress-tables.sql
    copy_in "$repo/DataAccessLibrary/Migrations/0001_core.sql" /work/0001_core.sql
    echo "The real common.sh and rights files are in /opt/minerbot in $name."

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
        timeout -k 10 270 docker exec "$name" bash /work/rights-test.sh --inside 2>&1 || code=$?
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
        echo "Usage: bash deploy/tests/rights-test.sh" >&2
        exit 1
        ;;
esac

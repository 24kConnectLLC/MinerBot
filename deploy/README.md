# Deploying MinerBot on the VPS

This folder holds everything the server runs MinerBot with: the compose file for the bot and the web dashboard, the
scripts that build, swap, roll back, set up and back them up, the dashboard's nginx file and its database rights, and
the tests of all of it. The server keeps its own names (database names, secrets) in files that never come from here.
`README.txt` is the owner's page on the server, installed as `/opt/minerbot/README.txt`.

## Files and where they go

| In this folder | On the server | Mode |
|---|---|---|
| `compose.yml`, `common.sh`, `dashboard-rights.sql`, `rights-list.sql`, `minerbot-dashboard.conf` | `/opt/minerbot/<same name>` (the nginx file is a reference copy: only `setup-dashboard.sh` puts it into nginx) | 600 |
| `deploy.sh`, `rollback.sh`, `setup-dashboard.sh`, `fill-secrets.sh`, `backup.sh`, `status.sh` | `/opt/minerbot/<same name>` | 700 |
| `README.txt` | `/opt/minerbot/README.txt`, with `{{ORIGIN_CERT_ENDS}}` filled in from the installed certificate, or "not installed yet" | 600 |
| `minerbot.cron` | `/etc/cron.d/minerbot` (cron refuses group- or world-writable files) | 644 |
| `bootstrap.sh`, `server.conf.example`, `README.md`, `tests/` | not installed | |

The server's layout, all of it root's:

```
/opt/minerbot/                700
  server.conf                 600  SITE_DB, BOT_DB, BOT_READY_PATTERN (bootstrap.sh writes it; never in Git)
  bot.env  dashboard.env  dashboard.env.previous    600
  compose.yml common.sh README.txt dashboard-rights.sql rights-list.sql minerbot-dashboard.conf   600
  deploy.sh rollback.sh setup-dashboard.sh fill-secrets.sh backup.sh status.sh                     700
  src/                        the clone deploy.sh builds from, detached at the deployed commit
  state/                      700: lock, lock.info, backup.lock, deploy-files.commit, bootstrap.done, nginx-change
                                   (only while an nginx change runs), nginx-backup/<UTC>/ (newest 3), nginx-check/,
                                   tmp/ (setup's answers and the bootstrap's staged files, only while they run)
  logs/                       700: <UTC>-<action>.log (newest 60), failed-<service>-<UTC>.log, history.log
  old/                        700: the files from before the move, kept as they were
/etc/cron.d/minerbot          644
/etc/nginx/conf.d/minerbot-dashboard.conf      644, no secret in it
/etc/nginx/minerbot/          700: origin-cert.pem, origin-key.pem, proxy-key.conf (600); .staging/ while a change runs
/srv/backups/minerbot/        700 (and /srv/backups 700): minerbot-<UTC>-<reason>.sql.gz (600), backup.log (600)
```

The real names live only on the server: in `server.conf`, and inside the connection strings in `bot.env` and
`dashboard.env`. `server.conf.example` shows the lines with placeholders.

### How files reach the server

`deploy.sh` installs a commit's deploy files with `install_deploy_files` in `common.sh`, which `bootstrap.sh` uses too:

1. The files come from the commit object (`git archive <commit> deploy`), never from `src/`'s working tree. Nothing
   ever runs from `src/`, because bash reads a script as it runs it.
2. **Forward only.** When `state/deploy-files.commit` names a commit that isn't an ancestor of the one being deployed
   (an older commit, or one on another line), the installed files stay, and the log says "Kept the deploy files of
   ...". Deploying an older commit to undo a bot change never takes the scripts or the rights file back.
3. Before anything is installed, the staged files are checked: every file is there; `bash -n` passes on every script;
   every script has `set -Eeuo pipefail` and `umask 077` and never turns tracing on; Compose accepts the compose file
   (`docker compose -f <stage>/compose.yml --project-directory /opt/minerbot config --quiet`); and the rights file
   fills in with this server's names and leaves no `{{`. A failure installs nothing and stops the deploy.
4. Each changed file is written beside its target and renamed over it (`mktemp`, copy, `chmod`, `mv -f`), so a script
   that's running, `deploy.sh` included, keeps reading its old copy. A file whose content is the same keeps its inode.
5. `state/deploy-files.commit` gets the commit.
6. When `deploy.sh` or `common.sh` changed, the deploy runs again once, in the same systemd unit, with the same log,
   the same lock and the same deadline: `exec env MINERBOT_REEXEC=<commit> /bin/bash /opt/minerbot/deploy.sh <its
   arguments>`. That run fetches nothing and builds exactly the commit whose files it installed. A run with
   `MINERBOT_REEXEC` set never runs again.
7. When the reference nginx file now differs from nginx's, the deploy ends with: "The nginx file changed. Run
   ./setup-dashboard.sh --no-questions to install it: it says what it will change, asks, and tests the file first."

## The scripts

Each is run as root from `/opt/minerbot`. Exit code 0 means done; 1 means refused or stopped, with a message that says
what happened, what state it left and what to do.

| Command | What it does | Runs as | Limit |
|---|---|---|---|
| `./deploy.sh [ref] [--only bot\|dashboard] [--no-start]` | Builds a commit on main (the default ref) and swaps it in, the dashboard first. Installs the deploy files, dumps and migrates when migrations are due, checks the dashboard's settings with its own validator, rehearses, applies and compares its database rights, then swaps by image ID. A service that isn't ready goes back to the exact image it ran. `--only` swaps one service; `--no-start` stops after the build and the migrations. | `minerbot-deploy` | 90 min |
| `./rollback.sh bot\|dashboard` | Swaps a service between the image it runs and `:previous`, by image ID. Running it again goes forward again. Never touches the database, the rights, nginx or the other service. | `minerbot-rollback` | 15 min |
| `./setup-dashboard.sh` | Asks for what only the owner has (the Origin certificate and key, the Discord client ID and secret, the owners and admins, Cloudflare Access's team and AUD), then changes only what differs. Enter keeps a value, `keep` keeps a paste. | `minerbot-setup` | 30 min |
| `./setup-dashboard.sh --no-questions [--yes]` | Keeps every value. Installs a changed nginx file, applies the rights again and repairs whatever differs. It asks "Go ahead?" unless `--yes`; without a terminal and without `--yes` it changes nothing. | `minerbot-setup` | 30 min |
| `--rotate-proxy-key`, `--rotate-db-password` | Makes a new proxy key or database password; goes with either form above. | | |
| `./setup-dashboard.sh --remove-nginx [--yes]` | The emergency path when `nginx -t` fails and names MinerBot's file: takes the file out of nginx, keeping a copy, tests and reloads. Works while `nginx -t` fails. Nothing else. | `minerbot-setup` | 30 min |
| `./fill-secrets.sh` | Asks for `BOTTOKEN`, `WEBHOOKCHANNEL`, `STEAMWEB_PUB_KEY` and `STEAMUSER_NAME`, hidden, Enter keeps; writes them into `bot.env` and prints the recreate command. Holds the lock while it runs. | terminal | 10 min a question |
| `./backup.sh nightly\|pre-deploy\|manual` | Dumps MinerBot's database into `/srv/backups/minerbot` and keeps 14 days, the newest 3 always. Prints nothing when it works and one line when it fails. | cron, deploy | its own limits, 2,400 s at most |
| `./status.sh` | A read-only look: containers, images, history, a running action, backups, the certificate, `nginx -t`, the disk. | terminal | 30 s a probe |
| `./status.sh --check` | The nightly check: one line per problem and exit 1, silence and exit 0 when all is well. Never changes anything. | cron | 600 s (cron line) |
| `./status.sh --mail-test` | Sends one message to root, to prove cron's mail reaches the owner. | terminal | 30 s |
| `./status.sh --nginx-look` | nginx's http-level lines, its main context and its build flags, never a secret. | terminal | 30 s a probe |

### Detached runs

Deploys, rollbacks, setup's changes and the bootstrap's changes run as a systemd unit of their own, started by
`run_detached` in `common.sh`. The unit keeps running when the terminal or SSH session goes away; systemd ends the
whole process tree at `RuntimeMaxSec`; and a second unit of the same name can't start while one runs. The unit gets
only `HOME`, `MINERBOT_DETACHED`, `MINERBOT_LOG` and `MINERBOT_DEADLINE` (the second 30 s before its limit), never the
caller's environment. Every step checks `time_left` against that deadline before a swap and before the tidy-up, so a
late run stops before a swap, never inside one.

The terminal follows the run's log, `logs/<UTC>-<action>.log`, and returns 0 when the log's last result is `RESULT ok`.
Ctrl-C or a closed window stops only the following: "The deploy goes on. Follow it again with: tail -f <log>". To come
back to the newest one: `tail -n 50 -f "$(ls -t logs/*-deploy.log | head -n 1)"`. A run that ends without a `RESULT` line
was stopped at its time limit, or the server restarted.

`logs/history.log` has a tab-separated line for each service a deploy swapped, and one closing line for each run:
`2026-11-06T03:12:44Z  deploy  dashboard  1a2b3c4d5e6f  ok  was 0f1e2d3c4b5a  log=logs/20261106T030832Z-deploy.log`.

## The move from the old files, once

`bootstrap.sh` moves `/opt/minerbot` from the files it had before this folder existed to these, without stopping the
bot. It runs once, with the owner's go-ahead, from the merged commit's own files:

```bash
cd /opt/minerbot
install -d -m 700 state state/tmp logs
timeout 120 git -C src fetch --quiet --prune --tags origin
c=$(timeout 10 git -C src rev-parse --verify 'origin/main^{commit}')
d=$(mktemp -d /opt/minerbot/state/tmp/bootstrap.XXXXXX)
timeout 60 git -C src archive "$c" deploy | timeout 60 tar -x -C "$d"
timeout 1100 bash "$d/deploy/bootstrap.sh" "$c" "$d"
```

It checks that every command the scripts use is there and that systemd enforces a unit's time limit, then, as the unit
`minerbot-bootstrap`: keeps the old files in `old/` once, writes `server.conf` from `bot.env`'s two connection strings,
reserves UID and GID 1654 as `minerbot-app`, asks Compose's own dry run whether the old `compose.yml` or the new one
would recreate the bot (both must say `Container minerbot-bot Running` and nothing else), installs the deploy files,
removes `setup-login.sh` and `move-docker-storage.sh` (their copies stay in `old/`), and checks the bot's container ID,
start time and label are unchanged. Every step is skipped when it's done, so a run cut off anywhere is finished by
running the same commands again. It never touches containers, images, env files or nginx.

To undo it before the dashboard is live: `cp -p old/compose.yml old/deploy.sh old/rollback.sh old/fill-secrets.sh
old/README.txt /opt/minerbot/`, then remove `/etc/cron.d/minerbot`, the new scripts and `state/bootstrap.done`. The bot's
container was never touched.

## When a setting changes

| What changes | What to do |
|---|---|
| Owners, admins, Discord client ID or secret, Access on or off, a new AUD, a new certificate | `./setup-dashboard.sh`: give the new value, Enter or `keep` for the rest. It recreates the dashboard or reloads nginx only as needed. |
| An admin or owner joins or leaves while Access is on | Their email in Cloudflare's Access policy (Zero Trust › Access › Applications › MinerBot admin), and their Discord ID with `./setup-dashboard.sh`. Both. |
| The proxy key or the dashboard's database password is suspected leaked | `./setup-dashboard.sh --no-questions --rotate-proxy-key` (or `--rotate-db-password`) |
| A PR changed the nginx file (deploy.sh says so) | `./setup-dashboard.sh --no-questions` (over SSH without a terminal, with the owner's go-ahead: `--no-questions --yes`) |
| The bot's token, webhook, Steam key or Steam user | `./fill-secrets.sh`, then `docker compose up -d --no-build --no-deps --force-recreate bot` |
| Anything else in bot.env | Edit it, with values only by the safe-character rule ("printable ASCII only; none of `$`, `"`, `'`, `\` and the backtick; no space at the start or the end; no `#` right after a space; spaces inside a value are fine"), then the same recreate |
| The database rights | A PR changes `deploy/dashboard-rights.sql`, and the next dashboard deploy applies it |
| The database names | `/opt/minerbot/server.conf` |

Settings are read only when a container is created: `docker restart` keeps the old values, so after editing a setting
by hand, run `docker compose up -d --no-build --no-deps --force-recreate <service>`, and always name the service.

The certificate's end date: `setup-dashboard.sh` prints it, `README.txt` has it (rendered on install, and again by
setup when the certificate changes), `status.sh` shows the days left, and the nightly check mails 30 days ahead.

## Rules for every script

- `set -Eeuo pipefail` and `umask 077`. `common.sh` holds constants and functions only, so sourcing it runs no command;
  every script sources the copy in its own folder.
- Every docker, mariadb, mariadb-dump, curl, git, openssl, nginx, ss, sendmail, systemctl and systemd-run call, and
  cPanel's `/scripts/restartsrv_nginx`, goes through `run_for [-k <grace>] <seconds>`, which is `timeout` with a 10-second
  grace. None of those names may stand where a command starts outside `run_for` itself; the tests check it.
- Every `read` has `-t`, and every `stty` runs only when `on_terminal` (`[ -t 0 ]`): without a terminal an unguarded
  `stty` fails, and in an EXIT trap under `set -e` it turns a finished run's status into 1.
- A named helper container (`minerbot-migrate`, `minerbot-dashboard-check`) is removed with `docker rm -f -v` before it
  starts and after it ends, since a run killed at its limit can leave one behind. The `-v` also removes the anonymous
  volume the dashboard image's `VOLUME /app/keys` gives the migration's container.
- Plain messages that say what happened, what state it left and what to do.
- Passwords go to MariaDB only on stdin, with `--skip-print-query-on-error`: otherwise its client prints a failing
  statement, password included.
- Never print a secret, and never turn tracing on.
- A value in an env file follows one safe-character rule, `env_safe` in `common.sh`: printable ASCII only; none of `$`,
  `"`, `'`, `\` and the backtick; no space at the start or the end; no `#` right after a space; spaces inside a value
  are fine. Within it, Compose's `env_file` and `docker run --env-file` take every value the same way. Outside it they
  don't: Compose expands `$`, cuts at ` #`, trims the ends and strips outer quotes.
- An EXIT or ERR trap never runs a command that can fail: under `set -e` that would change the run's exit status.
- No real names (the cPanel account, database names, the server's address or host name, Discord IDs) and no secrets.
  Use `<cpuser>`, `<sitedb>`, `<botdb>`, `<server IP>`.
- LF line ends (`.gitattributes` keeps them, even in a Windows checkout).
- Every `.sh` file, `common.sh` included, and every file in `tests/fakes/`, `record.sh` included, is 100755 in Git, and
  the tests check it. A Windows clone records 100644 instead, so add them with
  `git add --chmod=+x deploy/*.sh deploy/tests/*.sh deploy/tests/fakes/*`.

## nginx

- `minerbot-dashboard.conf` uses the account blocks' own listen lines: `listen 80;`, `listen [::]:80;`, `listen 443 ssl;`,
  `listen [::]:443 ssl;` and `http2 off;`. Never `default_server` or `reuseport`: cPanel's `default.conf` has both on
  these ports, and nginx refuses to load when either is repeated.
- Its includes of cPanel's Cloudflare list and of `proxy-key.conf` are patterns (`cloudflare.con[f]`), so a missing file
  makes nginx load without it, refusing every visitor here, instead of failing `nginx -t` for every site.
- It has no secret in it. The proxy key is one line in `/etc/nginx/minerbot/proxy-key.conf`, which only root can open.
- Only `setup-dashboard.sh` installs it, through `nginx_change` in `common.sh`: a marker and a backup first; then the
  candidate passes `nginx -t` on its own with the server's main context (its `user`, `load_module` and `include` lines,
  from `main_context`) and an absolute `-c` path, before it goes near `conf.d`; then the files are installed by rename,
  the `conf.d` file last; then `nginx -t` with every site; then cPanel's reload. A failure at any test or the reload puts
  every file back byte for byte. A change cut off in between leaves the marker `state/nginx-change`, and the next
  setup run undoes it first.
- Never add `brotli off;` or another directive for a module the server doesn't load: `nginx -t` would fail and block
  reloads for every site. The read-only look on 5 November 2026 found that cPanel's http level sets nothing the
  dashboard's blocks need to undo.
- Never share raw `nginx -T` output: it prints root-only files, MinerBot's proxy key and cPanel's own proxy secret among
  them. Use `./status.sh --nginx-look`, which skips every file that isn't world-readable and every line naming the proxy
  key's header or cPanel's.

## The database rights

`dashboard-rights.sql` says everything the dashboard's login, `minerbot-dashboard` on `localhost` and `127.0.0.1`, may
do. It's declarative: the login loses every right, then gets exactly these. Table and column rights only, and only what
today's code uses.

- Each deploy that swaps the dashboard, and each setup run, applies it with `apply_rights`: a rehearsal on a locked
  throwaway login (`minerbot-rights-check`), then the real login, then a comparison of the two through
  `rights-list.sql`, which never uses `SHOW GRANTS` (it prints the password's hash). A failed rehearsal leaves the real
  login as it was; a failed apply leaves it fewer rights, never more; a difference means a right `REVOKE` can't take
  away, such as a PROXY grant, which a person must remove.
- A `--only bot` deploy leaves the rights as they are, even after a migration: the running dashboard doesn't use the new
  tables, and a newer rights file could take away a right an older dashboard still uses.
- A pull request whose code needs another right adds it in the same pull request, after the migration that makes its
  table (MariaDB refuses a right on a table that doesn't exist yet), and changes `DashboardRightsTests`. A right is
  removed only in a later pull request than the one whose code stopped using it, so `rollback.sh`'s previous image
  always has what it needs.
- Never give this login rights in cPanel: cPanel only knows whole databases.

How the file grows. The approved plan's list is the ceiling:

| Pull request | Adds |
|---|---|
| PR 4, armor review | `SELECT`, `UPDATE (Status)` on `ihvph_armor_styles`; `SELECT, INSERT` on `armor_reviews` |
| PR 5, blacklist and audit page | `SELECT, INSERT, UPDATE, DELETE` on `ihvph_blacklist`; `SELECT` on `audit_log` |
| The server channels page | `SELECT, INSERT, UPDATE (armor_channel_id, map_channel_id, lobby_channel_id, announcements_channel_id)` on `ihvph_discord_servers` |
| The bot's command, switch and note tables | `INSERT` on `bot_commands`; `SELECT, INSERT, UPDATE, DELETE` on the dashboard's switches and notes, each in the PR that creates the table |

## The settings rule

`rollback.sh` keeps the current `dashboard.env`, so the previous image must accept it. A setting is never renamed or
removed in the pull request that stops using it: the old name stays accepted and written for one more pull request. A
new setting gets an empty-means-off default wherever it can, and setup never writes a value in a form the previous
image's validator refuses. The validators ignore settings they don't know, so a setting a newer pull request added
never stops an older image. The same rule is a comment at the top of `MinerBot.Dashboard/Settings/SettingsValidation.cs`.

## Backups and restore

`backup.sh nightly` runs at 03:17 from `/etc/cron.d/minerbot`; every deploy with migrations runs `backup.sh pre-deploy`
first. Each dump is a consistent InnoDB snapshot without locks (`--single-transaction`), with BINARY columns as hex,
utf8mb4, routines and events, and without the session rows (`--ignore-table-data`), so a restore signs everyone out. It
goes to a `.partial` file, is checked with `gzip -t` and for its last line `-- Dump completed`, and only then gets its
final name. Dumps older than 14 days are pruned after a dump that worked, the newest 3 always kept.

A failure is visible four ways: cron mails root (the cron line's `|| echo` also covers a run stopped at its limit);
`backup.log` has a `failed` line, or, after a restart or a kill, a `started` line with nothing after it; the 03:47
`status.sh --check` reports a newest backup older than 36 hours or a run that never finished; and every deploy's
preflight warns.

Restore, as `README.txt` gives it on the server:

1. `./backup.sh manual`, a dump of the current state first.
2. `docker compose stop dashboard`, and the bot too once it uses this database.
3. `gzip -t <file>`, and `gzip -dc <file> | tail -n 1` must show `-- Dump completed`.
4. To look first: `mariadb -e 'CREATE DATABASE mbrestorecheck'`, `gzip -dc <file> | mariadb mbrestorecheck`, look at
   `schema_version`, then `mariadb -e 'DROP DATABASE mbrestorecheck'`. The name has no `_`, so cPanel never maps it.
5. `gzip -dc <file> | mariadb --default-character-set=utf8mb4 <BOT_DB from server.conf>`. This replaces each table in
   the dump.
6. `docker compose up -d --no-build --no-deps dashboard`, then `./status.sh`.

## Tests

CI's required "Build and test" job runs, after the .NET tests:

| Step | What it catches |
|---|---|
| `shellcheck -x -P SCRIPTDIR deploy/*.sh deploy/tests/*.sh deploy/tests/fakes/*` | Quoting and syntax in scripts the server can't check |
| `docker compose -f deploy/compose.yml config --quiet` (with an empty `deploy/bot.env`, which `*.env` keeps out of Git) | YAML and schema mistakes |
| `bash deploy/tests/nginx-test.sh` | The nginx file in `nginx:1.31.8-alpine`, next to a cPanel-like configuration |
| `bash deploy/tests/nginx-procedure-test.sh` | The real `nginx_change`, `nginx_remove`, `nginx_undo_cut_off` and `nginx_look` against a real nginx 1.31.8 |
| `bash deploy/tests/rights-test.sh` | The real `apply_rights` against MariaDB 11.4 |
| `bash deploy/tests/scripts-test.sh` | The deploy, rollback, setup, bootstrap, fill-secrets, backup and status logic in `almalinux:9`, the server's bash and coreutils, with stand-ins for Docker, MariaDB, nginx, systemd and the rest |

Each test script starts only `--rm` containers with time limits, and removes its networks and anonymous volumes in a
trap. `deploy/tests/fixtures/compose-before-pr3.yml` is the server's compose file from before these deploy files, word for
word. `deploy/tests/local-stack.sh up|check|down` is a manual test of the whole stack (MariaDB, the dashboard image and
nginx sharing one loopback, as host networking does), including the bootstrap's bot check with real Compose; it isn't
in CI.

On this Windows machine, in Git Bash, with temporary files off the nearly full C: drive:

```bash
export TMPDIR=G:/Temp MSYS_NO_PATHCONV=1   # any folder on a drive with room, not C:
bash deploy/tests/nginx-test.sh
bash deploy/tests/nginx-procedure-test.sh
bash deploy/tests/rights-test.sh
bash deploy/tests/scripts-test.sh
timeout 120 docker run --rm -v "$(pwd -W):/mnt:ro" -w /mnt koalaman/shellcheck:stable -x -P SCRIPTDIR deploy/*.sh deploy/tests/*.sh deploy/tests/fakes/*
d=$(mktemp -d) && cp deploy/compose.yml "$d/" && touch "$d/bot.env" && timeout 60 docker compose -f "$d/compose.yml" config --quiet; rm -r "$d"
```

The compose check runs on a copy with an empty `bot.env` beside it, so no env file is left in the checkout.

Check `docker system df` first: the image build needs about 1 GB of Docker Desktop's disk. Then
`bash deploy/tests/local-stack.sh up` builds `mbtest-dashboard:local` as the server builds the dashboard
(`docker build --network host --cpu-quota 200000 --cpu-period 100000 -f MinerBot.Dashboard/Dockerfile`), which proves
the Dockerfile and the CPU limit with a real .NET build. Then `check`, and `down --images`, which removes the image and
exactly the build cache that build made. Use the same TMPDIR for all three. The stack stops by itself 2 hours after
`up`.

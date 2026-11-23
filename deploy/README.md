# Deploying MinerBot on the VPS

This folder holds everything the server runs MinerBot with: the compose file for the bot and the web dashboard, the
scripts that build, swap, roll back, set up and back them up, the dashboard's nginx file and its database rights, and
the tests of all of it. The server keeps its own names (database names, secrets) in files that never come from here.
`README.txt` is the owner's page on the server, installed as `/opt/minerbot/README.txt`.

## Files and where they go

| In this folder | On the server | Mode |
|---|---|---|
| `compose.yml`, `common.sh`, `dashboard-rights.sql`, `rights-list.sql`, `minerbot-dashboard.conf` | `/opt/minerbot/<same name>` (the nginx file is a reference copy: only `setup-dashboard.sh` puts it into nginx) | 600 |
| `deploy.sh`, `rollback.sh`, `setup-dashboard.sh`, `fill-secrets.sh`, `backup.sh`, `status.sh`, `request.sh` | `/opt/minerbot/<same name>` | 700 |
| `README.txt` | `/opt/minerbot/README.txt`, with `{{ORIGIN_CERT_ENDS}}` filled in from the installed certificate, or "not installed yet" | 600 |
| `minerbot.cron` | `/etc/cron.d/minerbot` (cron refuses group- or world-writable files) | 644 |
| `minerbot-dashboard.tmpfiles` | `/etc/tmpfiles.d/minerbot-dashboard.conf`, from which systemd-tmpfiles makes the dashboard's socket folder at every boot | 644 |
| `bootstrap.sh`, `move-database.sh`, `server.conf.example`, `README.md`, `tests/` | not installed; `move-database.sh` runs from `src/deploy/` | |

The server's layout, all of it root's but the socket's folder:

```
/opt/minerbot/                700
  server.conf                 600  SITE_DB, BOT_DB, BOT_READY_PATTERN (bootstrap.sh writes it; never in Git)
  bot.env  dashboard.env  dashboard.env.previous    600
  compose.yml common.sh README.txt dashboard-rights.sql rights-list.sql minerbot-dashboard.conf   600
  deploy.sh rollback.sh setup-dashboard.sh fill-secrets.sh backup.sh status.sh request.sh          700
  src/                        the clone deploy.sh builds from, detached at the deployed commit
  state/                      700: lock, lock.info, backup.lock, deploy-files.commit, bootstrap.done,
                                   dashboard-rights.applied.sql (600, the rights file the last apply used),
                                   database-moved (600, after move-database.sh; database-move,
                                   database-move-values and database-move-ran only while a move runs, or after
                                   one stopped, until a run finishes it),
                                   compose-ready-bot.yml, compose-ready-dashboard.yml (600, the compose.yml each
                                   service was last ready under), nginx-change (while an nginx change runs, or after
                                   one was cut off), nginx-backup/<UTC>/ (newest 3), nginx-check/,
                                   tmp/ (what a run works with while it runs: setup's answers, the staged deploy files,
                                   the rights file a dashboard swap applies with GRANTs added, and so on)
  logs/                       700: <UTC>-<action>.log (newest 60), failed-<service>-<UTC>.log, history.log
  old/                        700: the files from before the move, kept as they were
/etc/cron.d/minerbot          644
/etc/tmpfiles.d/minerbot-dashboard.conf        644: d /run/minerbot-dashboard 2750 1654 nobody -
/run/minerbot-dashboard/      2750, owner 1654, group nobody: dashboard.sock (660) while the dashboard runs
/etc/nginx/conf.d/minerbot-dashboard.conf      644, no secret in it
/etc/nginx/minerbot/          700: origin-cert.pem, origin-key.pem, proxy-key.conf, pipelog.conf (600);
                                   .staging/ while a change runs
/etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog   755: cPanel's hook for pipelog.conf (see nginx below)
/var/log/nginx/minerbot-dashboard.access.log, minerbot-dashboard.error.log
/srv/backups/minerbot/        700 (and /srv/backups 700): minerbot-<UTC>-<reason>.sql.gz (600), backup.log (600)
```

The real names live only on the server: in `server.conf`, and inside the connection strings in `bot.env` and
`dashboard.env`. `server.conf.example` shows the lines with placeholders.

### How files reach the server

`deploy.sh` installs a commit's deploy files with `install_deploy_files` in `common.sh`, which `bootstrap.sh` uses too:

1. The files come from the commit object (`git archive <commit> deploy`), never from `src/`'s working tree. Nothing
   runs from `src/`, because bash reads a script as it runs it, but the one-off `move-database.sh`, which holds the lock
   that every deploy waits for, so nothing changes `src/` while it runs.
2. **Forward only.** The installed files stay when `state/deploy-files.commit` names a newer commit than the one being
   deployed ("Kept the deploy files of ..., which are newer than ...") or one on another line of main ("Kept the
   deploy files of ..., which is on main, on another line than ..."). When that commit is gone from `src/`, or is no
   longer on main, main's history was rewritten, and the deployed commit's files replace them, with a line that says
   so. Deploying an older commit never takes the scripts or the rights file back; its dashboard gets the rights
   "The database rights" describes.
3. Before anything is installed, the staged files are checked: every file is there; `bash -n` passes on every script;
   every script but `common.sh` has `set -Eeuo pipefail` and `umask 077`, and none turns tracing on; Compose accepts the
   compose file (`docker compose -f <stage>/compose.yml --project-directory /opt/minerbot config --quiet`); and the
   rights file fills in with this server's names and leaves no `{{`. A failure installs nothing and stops the deploy.
4. Each changed file is written beside its target and renamed over it (`mktemp`, copy, `chmod`, `mv -f`), so a script
   that's running, `deploy.sh` included, keeps reading its old copy. A file whose content is the same keeps its inode.
5. `state/deploy-files.commit` gets the commit.
6. `systemd-tmpfiles --create /etc/tmpfiles.d/minerbot-dashboard.conf` (30 s) makes the dashboard's socket folder, or
   puts its mode and owners right, also when the files were kept. A failure stops the deploy before any build:
   "systemd-tmpfiles couldn't make /run/minerbot-dashboard, the folder of the dashboard's socket (above). Nothing was
   swapped. Run this again once that's fixed."
7. When `deploy.sh` or `common.sh` changed, the deploy runs again once, in the same systemd unit, with the same log,
   the same lock and the same deadline: `exec env MINERBOT_REEXEC=<commit> /bin/bash /opt/minerbot/deploy.sh <its
   arguments>`. That run fetches nothing and builds exactly the commit whose files it installed. A run with
   `MINERBOT_REEXEC` set never runs again.
8. When the reference nginx file differs from nginx's and the dashboard runs the commit of the installed deploy files,
   the deploy ends with: "The nginx file changed. Run ./setup-dashboard.sh --no-questions to install it: it says what
   it will change, asks, and tests the file first." nginx's file moves only with the dashboard: after a `--only bot`
   or `--no-start` deploy, a deploy that stopped before the dashboard's swap, or a rollback, the new file waits, and
   the note shows after that commit's dashboard deploy.

## The scripts

Each is run as root from `/opt/minerbot`. Exit code 0 means done; 1 means refused or stopped, with a message that says
what happened, what state it left and what to do.

| Command | What it does | Runs as | Limit |
|---|---|---|---|
| `./deploy.sh [ref] [--only bot\|dashboard] [--no-start]` | Builds a commit main itself pointed at (main's tip by default) and swaps it in, the dashboard first. Installs the deploy files, dumps and migrates when migrations are due, checks the dashboard's settings with its own validator, rehearses, applies and compares its database rights, then swaps by image ID. A swap that fails puts the service back on the exact image it ran. `--only` swaps one service; `--no-start` stops after the build and the migrations. | `minerbot-deploy` | 90 min |
| `./rollback.sh bot\|dashboard` | Swaps a service between the image it runs and `:previous`, by image ID. Running it again goes forward again. When the service's container has another image than `:current`, a swap was cut off after it moved the tags, or Compose didn't put a failed swap back: then it finishes that swap on `:current` first, and moves no tag. Never touches the database, the rights, nginx or the other service. | `minerbot-rollback` | 15 min |
| `./setup-dashboard.sh` | Asks for what only the owner has (the Origin certificate and key, neither shown as it's pasted, the Discord client ID and secret, the owners and admins, Cloudflare Access's team and AUD), then changes only what differs. Enter keeps a value, `keep` keeps a paste. It makes the proxy key, the request-signing key and the database password itself, and recreates a bot that runs on another request-signing key than `bot.env` holds ("The bot's requests"). | `minerbot-setup` | 30 min |
| `./setup-dashboard.sh --no-questions [--yes]` | Keeps every value. Installs a changed nginx file once the dashboard of its commit runs, applies again the rights the last apply used, starts the dashboard when its container is stopped, recreates it when it runs on older settings than `dashboard.env` holds (after a recreate that failed), recreates the bot when it runs on another request-signing key than `bot.env` holds, and repairs whatever differs. It asks "Go ahead?" unless `--yes`; without a terminal and without `--yes` it changes nothing. | `minerbot-setup` | 30 min |
| `--rotate-proxy-key`, `--rotate-db-password`, `--rotate-request-key` | Makes a new proxy key, database password or request-signing key; each goes with either form above. | | |
| `./setup-dashboard.sh --remove-nginx [--yes]` | The emergency path when `nginx -t` fails and names MinerBot's file: undoes a cut-off change first, takes the file out of nginx, keeping a copy, tests and reloads. Works while `nginx -t` fails. Nothing else: `/etc/nginx/minerbot`, with `pipelog.conf`, and cPanel's hook stay. | `minerbot-setup` | 30 min |
| `./fill-secrets.sh` | Asks for `BOTTOKEN`, `WEBHOOKCHANNEL`, `STEAMWEB_PUB_KEY` and `STEAMUSER_NAME`, hidden, Enter keeps; writes them into `bot.env` and prints the recreate command. Holds the lock while it runs. | terminal | 10 min a question |
| `./backup.sh nightly\|pre-deploy\|pre-move\|manual` | Dumps MinerBot's database into `/srv/backups/minerbot` and keeps 14 days, the newest 3 always. Prints nothing when it works and one line when it fails. | cron, deploy | its own limits, 2,400 s at most |
| `bash src/deploy/move-database.sh [--drop-old]` | Once: moves MinerBot's database out of cPanel, and later deletes the old one through cPanel ("MinerBot's database out of cPanel"). | unit `minerbot-move-database` | 900 s |
| `./request.sh <kind> [--guild <server ID>] [--channel <channel ID>] [--text <text>]` | Sends the bot one of the dashboard's requests, through the dashboard's own code in its image, and prints the bot's answer; after a restart, waits until the bot is back on Discord. `./request.sh --list` lists the kinds. Holds the lock until the bot has answered ("The bot's requests"). | terminal | 900 s |
| `./status.sh` | A read-only look: containers, images, history, a running action, backups, the certificate, `nginx -t`, cPanel's Cloudflare list, the dashboard's socket, its proxy key while MinerBot's file is in nginx, the public home page's status, the request-signing key in both env files and both containers, the disk. | terminal | 30 s a probe |
| `./status.sh --check` | The nightly check, at 05:47: one line per problem and exit 1, silence and exit 0 when all is well. Never changes anything. | cron | 600 s (cron line) |
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
A swap's word is `ok`, `not ready` or `compose failed`; a run's closing word is `ok`, `built` (`--no-start`) or
`failed`, with a note such as `stopped while swapping the bot`. An image is named by the first 12 characters of the
commit in its revision label, or `image <first 12 of its ID>` without one, as the bot's images from before these
scripts have.

### Which commits deploy

Root runs these scripts, and they come from the deployed commit, so only states that main itself had are deployed.
`deploy.sh` fetches with `git fetch --prune --prune-tags --force --tags origin` (2 minutes), so GitHub's tags win: a tag
moved there deploys at its new place, and one deleted there is gone. It looks the ref up by its full name: a branch on
GitHub (`refs/remotes/origin/<ref>`), then a tag (`refs/tags/<ref>`), then a commit ID of 4 to 40 hex characters, so
a tag can never stand in for a branch. The commit must then be one that main's tip pointed at, on main's first-parent
line. This lists them, and going back means picking from that list:

```bash
git -C /opt/minerbot/src log --first-parent --oneline refs/remotes/origin/main
```

A commit inside a merged pull request is never on it, since it was never reviewed on its own: "<12> (<ref>) isn't one
of main's own commits: it isn't merged into main, or it's inside a merged pull request, where it was never reviewed on
its own. ...". A fetch that fails says "Couldn't fetch from GitHub within 2 minutes; git's reason is above. Nothing was
changed.", with git's own message above it.

This holds only while pull requests are merged with a merge commit or squashed. A rebase merge, or a push of several
commits to main at once, puts each of those commits on main's first-parent line, where `deploy.sh` accepts it: keep
"Allow rebase merging" off in the repository's Settings › General › Pull Requests.

### When a swap fails

A swap counts as failed when Compose exits non-zero, when the container doesn't then run the new image, or when the
service isn't ready. The service then goes back to the exact image it ran, and only that service: `:current` and
`:previous` go back to what they were, the new image is tagged `:failed`, its container's last 200 log lines go to
`logs/failed-<service>-<UTC>.log` (600, never to the screen: the bot's can hold a Steam sign-in code), and the deploy
stops there.

When Compose can't put the service back either, as when Docker doesn't answer, the deploy says so and leaves the
service as Compose left it. Still on the new image: "... and Compose didn't put it back on <rev> (above), so it's left
on <rev>, while minerbot-<service>:current names <rev>. ... Run ./rollback.sh <service> once Docker answers: it
finishes the swap back.", as it does a swap cut off after it moved the tags (below). With its container gone: "... and
its container is gone, so it doesn't run. ... Start it on <rev>, which minerbot-<service>:current names, with: docker
compose up -d --no-build --no-deps <service>." The swap's history line then ends `not put back on <rev>, left on <rev>`,
or `not put back on <rev>, not running`.

Each service keeps the `compose.yml` it was last ready under as `state/compose-ready-<service>.yml`, written each time
it's ready after a deploy's swap, an already-on-it check or a rollback, and, for the dashboard, after setup recreated or
started it. Setup's recreate counts only when Compose exits 0 and the container is a new one, since one that failed can
leave the old container running on its old settings, ready all the same. When a failed swap's `compose.yml` gives the
service other settings than that copy, by Compose's own `docker compose config --hash <service>`, the copy goes back
in place before the old image starts, so it runs as it did: "compose.yml changed the <service>'s settings since it was
last ready, so the compose.yml it was ready under is back in place, until the next deploy installs the new one again."
The history line then ends `, under the earlier compose.yml`.

Ready means, for the dashboard, within 90 seconds: its container runs the image it must, with no new restart, and isn't
unhealthy; its socket is as nginx needs it; `/health/ready`, asked through the socket, answers `Healthy`, which proves
both databases with its own login and rights; and its home page, asked through the socket as nginx asks for it, with the
proxy key its container runs with (or `dashboard.env`'s when Docker doesn't say), answers 200, which proves it can
render a page. `/health/ready` renders none. On 2026-11-12, the deploy that first started the dashboard on the server,
and a rebuild deployed after it, each started a dashboard that couldn't read its own stylesheet: every page answered 500
while `/health/ready` answered `Healthy`, and both were called ready. When the public home page answered 200 before a
deploy's swap, it must answer 200 again within 30 seconds, but no other readiness check looks at the public page, and
`status.sh --check` looks at it only once a night. A first swap (503 before it), a swap over a dashboard that already
fails, or one whose request Cloudflare challenges has nothing to compare. A failed swap's put-back, a deploy that finds
the dashboard already on its image, `rollback.sh`, and setup's start and recreate don't look at the public page at all.
That's why the page check goes through the socket. For the bot: its image's own health check says healthy within 150
seconds, and 20 seconds later it still runs with the same start time and no new restart. The bot rewrites a heartbeat
file every 5 seconds while it's up on Discord, and `dotnet "MinerBot 2.0.dll" healthcheck` passes while that's at most a
minute old, so healthy means the bot reached Discord. Unhealthy fails at once. An image without a health check, from
before the heartbeat, which `rollback.sh` may run, is ready instead when its log has a line matching
`BOT_READY_PATTERN` from `server.conf`. A container on another image than the one it must run fails at once.

The bot's own status, for the owner's check after a deploy: its health and the health check's last answers,

```bash
docker inspect -f '{{.State.Health.Status}}' minerbot-bot
docker inspect -f '{{json .State.Health}}' minerbot-bot
```

and what it recorded in MinerBot's database, where every time is UTC, so `UTC_TIMESTAMP()`, never `NOW()`, which is
the server's Eastern time. The heartbeat is under 60 seconds old while the bot runs, and a Steam reconnect shows up as
`steam.offline` and `steam.logged_on` events, or `steam.logged_off` and `steam.logged_on` when Steam logged the bot off
first:

```bash
mariadb <BOT_DB from server.conf> -e "SELECT heartbeat_at, TIMESTAMPDIFF(SECOND, heartbeat_at, UTC_TIMESTAMP(3)) AS seconds_old, version, discord_state, steam_state, stopped_at FROM bot_status"
mariadb <BOT_DB from server.conf> -e "SELECT at, kind, level, detail FROM bot_events ORDER BY id DESC LIMIT 20"
```

and its copy of the servers it's in, which the dashboard's servers pages read: the servers whose `left_at` is NULL
are the ones the bot is in, as many as `discord_guilds` in `bot_status`, which counts Discord's own list while the bot
is connected, each with `available` 1 unless Discord has an outage. A text channel the bot can see, made in a test
server, shows up in `guild_channels` within 15 seconds. Only text and announcement channels are kept, and one the bot
can't see comes from Discord named `___hidden___`, and is kept so, with `can_view` 0. The names are Discord's and can
hold emoji, so these read them as utf8mb4:

```bash
mariadb --default-character-set=utf8mb4 <BOT_DB from server.conf> -e "SELECT guild_id, name, left_at, available FROM guilds"
mariadb <BOT_DB from server.conf> -e "SELECT (SELECT COUNT(*) FROM guilds WHERE left_at IS NULL) AS servers_in, discord_guilds FROM bot_status"
mariadb --default-character-set=utf8mb4 <BOT_DB from server.conf> -e "SELECT channel_id, name, type, can_view, can_send, can_embed, TIMESTAMPDIFF(SECOND, updated_at, UTC_TIMESTAMP(3)) AS seconds_old FROM guild_channels WHERE guild_id = <the test server's ID>"
```

The dashboard's status and servers pages show what those tables hold, so after a deploy that changes them the owner checks
them against the same: the Overview's state (online, late, offline or stopped) and its last check-in against `./status.sh`
and `heartbeat_at` above, and each of its banners against what it names, or "Nothing needs you right now." when nothing
does; Bot's uptime against `uptime_daily` (a day the 04:30 Eastern clean-up hasn't totalled yet, today always, counts
from `uptime_samples`, and a minute without a sample counts as down from the first minute recorded on), its jobs against
`job_status` and `job_runs`, and its events against `bot_events`, where the reconnects Discord asks for, the
`discord.disconnected` rows whose detail is `GatewayReconnectException`, show only with "Show routine reconnects"
ticked; Servers against Discord's own list and `guilds`, as many in the default list as `servers_in` above; and one
server's page against its channels in Discord, in Discord's order, with each setting's channel and what the bot can do
there. A setting that names a voice channel shows "Can't check", since the copy keeps only text and announcement
channels, and that isn't a problem: the bot's posts reach a voice channel's chat. A forum or a deleted channel shows
"Can't check" too, and the bot's posts reach neither: for armor, maps and lobbies its log says
"that channel isn't in that server" whenever it has one to post there, so look in Discord at what each "Can't check"
setting names. A status box left open refreshes every 30 seconds; during a swap nginx answers 503 for a moment, so the
box stops and says "Not updating: reload the page".

```bash
mariadb <BOT_DB from server.conf> -e "SELECT day, sampled_minutes, discord_minutes, steam_minutes FROM uptime_daily ORDER BY day DESC LIMIT 7"
mariadb <BOT_DB from server.conf> -e "SELECT job, schedule, last_started_at, last_result, last_detail, next_run_at FROM job_status"
```

`rollback.sh` puts its tags back when Compose never replaced the container: "Compose didn't recreate the <service> on
<rev> (above), so it still runs <rev>, and its tags are back as they were. Nothing was changed." A rollback onto an
image that isn't ready either is left there, and says so.

A deploy or rollback cut off after it moved the tags, by a restart of the server or a stop of its unit, leaves the
container on another image than `:current`, as does a failed swap Compose didn't put back. The next
`./rollback.sh <service>` finishes that swap on `:current` first: "The <service>'s container has <rev>, but
minerbot-<service>:current is <rev>: a deploy or rollback was cut off after it moved the tags, before Compose swapped
the container, or Compose didn't put a failed swap back. This run finishes that swap on <rev>, and moves no tag." It
removes the service's containers, Compose's half-made `<12 hex>_minerbot-<service>` among them (the volumes stay),
starts it and checks that it's ready. Running it once more goes back to `:previous`.

### Undoing a deploy

`./rollback.sh <service>` undoes a deploy only after the deploy's swap of that service completed. Take what the deploy
did from its log's last `RESULT` line, and from `./status.sh` once that says "Running now: nothing", never from
`deploy.sh`'s exit code, which is 1 for a failure, a run cut off and a dropped SSH session alike. Then, by the images
`./status.sh` shows for the container and the tags:

- The container and `:current` on the deployed commit, and `:previous` on the one before: `./rollback.sh <service>`
  puts the one before back.
- Both still on the commit from before, since the deploy stopped before its swap or a failed swap was put back: there's
  nothing to undo. `./rollback.sh` would put the image from before that one live, and `./status.sh --check` would stay
  clean.
- The container and `:current` on different commits, after a swap that was cut off or that Compose didn't put back:
  one `./rollback.sh <service>` finishes that swap onto `:current` and moves no tag, as above. Then read `./status.sh`
  again.
- No container: `docker compose up -d --no-build --no-deps <service>`, which starts `:current`.
- A rollback that landed one too far: `./rollback.sh <service>` once more goes forward again.

### The nightly check

`./status.sh --check` runs at 05:47 from `/etc/cron.d/minerbot`. It reports, one line each:

- no dump newer than 36 hours, a last backup that failed, or one that started more than 2 hours ago and never wrote
  another line;
- the Origin certificate ending within 30 days;
- `nginx -t` failing twice, a minute apart, so a reload in progress isn't reported;
- the bot not running, unhealthy (its heartbeat over a minute old, so it isn't up on Discord; an image without a
  health check isn't reported), its container missing, or Docker not answering;
- with `dashboard.env` there: the dashboard not running or unhealthy; its socket not as nginx needs it (a missing
  socket only while the dashboard runs); and the public home page answering anything but 200 twice, a minute apart,
  while the dashboard runs. A Cloudflare challenge (`cf-mitigated: challenge`, as Bot Fight Mode may send the server)
  isn't reported, since it says nothing about the dashboard;
- with `dashboard.env` there and MinerBot's file in nginx too: `/etc/nginx/minerbot/proxy-key.conf` missing, or not
  holding the proxy key the dashboard runs with (its container's, or `dashboard.env`'s when there's no container); and
  nginx's workers older than that file, so nginx hasn't loaded it (only when the file is right and no nginx change is
  cut off). After `./setup-dashboard.sh --remove-nginx`, neither is looked at until setup puts the file back;
- with `dashboard.env` there: the request-signing key missing from `bot.env` or `dashboard.env`, or not 64 hexadecimal
  digits in small letters there, the two files holding different keys, or the bot or the dashboard running with another
  key than its file holds, each line saying what to run and never the key;
- cPanel's Cloudflare list missing or empty;
- an nginx change that was cut off;
- with MinerBot's file in nginx: `/etc/nginx/minerbot/pipelog.conf` not matching cPanel's pipelog module (`pipelog
  off;` while the module loads, empty while it doesn't), and cPanel's hook missing while cPanel's folder for it is
  there;
- a trigger, event or stored routine in MinerBot's database, which the next deploy and setup refuse (nothing is said
  when MariaDB doesn't answer);
- less than 5 GiB free on `/srv`, or less than 2 GiB on `/`.

Its second looks at `nginx -t` and the public home page come a minute after the first, so a run with problems takes a
few minutes, well inside the cron line's 600 s. An outside uptime monitor on `https://bot.murderminershub.com/` covers
what the server can't see from itself.

## First setup

The scripts check most of what they need, and each one prints the next step and refuses to run out of order. These
steps they can't check, so they're here, in their order. `<server IP>` stands for the server's address.

1. **GitHub.** Keep "Allow rebase merging" off in the repository's Settings › General › Pull Requests, and merge pull
   requests with a merge commit or squashed ("Which commits deploy").
2. **Discord Developer Portal.** An application of its own, "MinerBot Dashboard", with no bot user. OAuth2 ›
   Redirects: `https://bot.murderminershub.com/signin-discord`. The dashboard asks only for the `identify` scope. The
   client ID isn't secret; the client secret comes right before setup (step 6). Every owner and admin turns on
   Discord's two-factor: the dashboard lets in nobody without it.
3. **Cloudflare, zone `murderminershub.com`, in this order:**
   - A **Configuration Rule** "MinerBot dashboard": when Hostname equals `bot.murderminershub.com`, SSL **Strict**,
     Rocket Loader off, Email Obfuscation off, Disable RUM and Disable Zaraz on. Create it before the DNS record:
     under a zone default of Flexible, Cloudflare would ask port 80, whose redirect to https would then loop. Strict is
     the one with no fallback: under Full, Cloudflare stops checking the origin's certificate, and nothing shows it.
     The other four rewrite the dashboard's pages or add scripts to them, which its Content-Security-Policy refuses.
   - A **Cache Rule** "MinerBot dashboard never cached": the same hostname, Bypass cache, moved to the bottom, where it
     wins over any other Cache Rule that matches. It backs up the dashboard's own `no-store`.
   - A **Compression Rule** "MinerBot dashboard not compressed" (Rules › Compression Rules): the same hostname, Disable
     compression. The pages hold secrets next to text visitors control, so nothing may compress them. It backs up the
     dashboard's `Cache-Control: no-transform` and nginx's `gzip off`.
   - **DNS:** an A record `bot`, `<server IP>`, Proxied, TTL Auto. Without the proxy it fails: browsers don't trust the
     Origin certificate, and nginx refuses every address but Cloudflare's.
   - **Access,** recommended: Zero Trust › Access › Applications › Self-hosted "MinerBot admin", domain
     `bot.murderminershub.com`, path `admin`, Allow › Emails of the owners and admins, One-time PIN, a 24-hour
     session. Its team domain and AUD tag are setup's last two answers.
   - Optional, only if the zone's free rule is unused: a WAF rate-limiting rule that blocks an IP for 10 seconds when
     it sends more than 20 requests to a path starting with `/signin` in 10 seconds.
   - Nothing zone-wide may cache or change the dashboard's pages: no Cache Everything or Edge Cache TTL rule that
     matches every subdomain, no Automatic Platform Optimization, no Workers route, and no Transform or Origin Rule on
     `*murderminershub.com/*`.
4. **Root's mail:** the server contact email in WHM's Basic WebHost Manager Setup is one the owner reads. Backup
   failures and the nightly check go there; `./status.sh --mail-test` proves it once the move is done.
5. **On the server:** the move (below), then `./deploy.sh main --only dashboard --no-start`, which builds the dashboard
   and makes its tables. On a new server, make MinerBot's database and the bot's login outside cPanel first ("MinerBot's
   database out of cPanel, once").
6. **Right before setup,** the two pages that show a secret only once, kept open until setup has its answers:
   - Cloudflare's SSL/TLS › Origin Server › Create Certificate: the key generated by Cloudflare, RSA 2048, hostnames
     **only** `bot.murderminershub.com` (setup refuses any other), 15 years, PEM;
   - Discord's MinerBot Dashboard › OAuth2 › Client Secret › Reset Secret.
7. `./setup-dashboard.sh`, then `./deploy.sh main --only dashboard`, which starts the dashboard.
8. **An uptime monitor from outside** on `https://bot.murderminershub.com/`, until `/status/ping` exists. The nightly
   check asks that page from the server itself, and when Bot Fight Mode challenges the server's request it can't tell
   whether visitors get through.

## The move from the old files, once

`bootstrap.sh` moves `/opt/minerbot` from the files it had before this folder existed to these, without stopping the
bot. It runs once, with the owner's go-ahead, from the merged commit's own files:

```bash
cd /opt/minerbot
install -d -m 700 state state/tmp logs
timeout 120 git -C src fetch --prune --prune-tags --force --tags origin
c=$(timeout 10 git -C src rev-parse --verify 'refs/remotes/origin/main^{commit}')
d=$(mktemp -d /opt/minerbot/state/tmp/bootstrap.XXXXXX)
timeout 60 git -C src archive "$c" deploy | timeout 60 tar -x -C "$d"
timeout 1100 bash "$d/deploy/bootstrap.sh" "$c" "$d"
```

It checks that every command the scripts use is there and that systemd enforces a unit's time limit, then, as the unit
`minerbot-bootstrap` (15 minutes at most): keeps the old files in `old/` once, writes `server.conf` from `bot.env`'s two
connection strings, reserves UID and GID 1654 as `minerbot-app`, asks Compose's own dry run whether the old
`compose.yml` or the new one would recreate the bot (both must say `Container minerbot-bot Running` and nothing else,
and both must give the bot the same config hash), installs the deploy files and makes the dashboard's socket folder,
removes `setup-login.sh` and `move-docker-storage.sh` (their copies stay in `old/`), and checks the bot's container ID,
start time and label are unchanged. A dry run that fails, doesn't finish within 60 seconds or prints lines the check
doesn't know stops the move with its own message, as one that would recreate the bot does, and nothing is installed.
Every step is skipped when it's done, so a run cut off anywhere is finished by running the same commands again. The
staging folder goes in every outcome but a SIGKILL. It never touches containers, images, env files or nginx.

To undo it before `./setup-dashboard.sh` has run, that is while `/etc/nginx/conf.d/minerbot-dashboard.conf` doesn't
exist:

```bash
cd /opt/minerbot
cp -p old/compose.yml old/deploy.sh old/rollback.sh old/fill-secrets.sh old/README.txt /opt/minerbot/
rm -f common.sh setup-dashboard.sh backup.sh status.sh dashboard-rights.sql rights-list.sql minerbot-dashboard.conf \
    state/bootstrap.done state/deploy-files.commit /etc/cron.d/minerbot /etc/tmpfiles.d/minerbot-dashboard.conf
rm -rf /run/minerbot-dashboard
```

The bot's container was never touched. `server.conf`, `old/`, `state/`, `logs/` and the reserved account `minerbot-app`,
which can't sign in, can stay: the old files never read them. `setup-login.sh` and `move-docker-storage.sh` stay in
`old/`: both were one-offs. A `--no-start` deploy also left the image `minerbot-dashboard:new`, which
`docker rmi minerbot-dashboard:new` removes, and the dashboard's tables in MinerBot's database, which can stay.

## MinerBot's database out of cPanel, once

A cPanel account can take over every database and login its map lists, and so can anything that runs as that account,
such as its websites' code: change a login's password with cPanel's own tools, then read the audit log, with the
addresses people signed in from, and change or delete what the dashboard writes. MinerBot's database was made in cPanel,
with the bot's login, so `move-database.sh` moves it out once: to `minerbot`, with the bot's own login `minerbot-bot`
on `localhost` and `127.0.0.1`, both root's alone. cPanel can never claim either name: it maps a database to the account
whose name and an underscore start it, and no account's name holds a hyphen. The dashboard's login, `minerbot-dashboard`,
was never cPanel's.

Nothing is copied from the old database. It's the cPanel account's, so its tables, views and even its names may be
that account's doing, and root would run them while it copied. The new database gets its tables from the migrations of
the dashboard that runs, and starts empty: the old rows stay in the dump the move makes first, and whoever was signed in
signs in again.

It runs from the checkout deploy.sh keeps, once a deploy has installed the same commit's deploy files, with the owner's
go-ahead:

```
bash /opt/minerbot/src/deploy/move-database.sh
```

As the unit `minerbot-move-database`, for 15 minutes at most, it:

1. checks that `server.conf`'s `BOT_DB` is a cPanel database, that `bot.env`'s and `dashboard.env`'s
   `ConnectionStrings__BotConnection` name it, that `minerbot-dashboard:current` is there, and that nobody else made
   `minerbot` or `minerbot-bot`;
2. dumps the old database, as `minerbot-<UTC>-pre-move.sql.gz`;
3. makes `minerbot` (utf8mb4) and `minerbot-bot`, with a new password and the rights the migrations need there (SELECT,
   INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP and REFERENCES) and none anywhere else, and checks the login;
4. runs the migrations of `minerbot-dashboard:current` into `minerbot` with that login;
5. keeps the three values it's about to change, then points them at `minerbot`: `dashboard.env`'s connection string gets
   the new database, `bot.env`'s the new database, login and password, and `server.conf`'s `BOT_DB` the new name;
6. moves the dashboard's rights there, with the rights file the last apply used, and recreates the dashboard, which must
   be ready, its home page included. Until then it runs on the old database, untouched.

The bot's login can make no view, routine, trigger or event: root's own scripts read MinerBot's database, and any of
those would run code there with root's rights. The bot's cPanel login stays, for the website's database, and must never
get a right in `minerbot`.

A failure before the dashboard runs on `minerbot` puts back those three values and nothing else, so a change made since
with setup or `fill-secrets.sh` stays, and the dashboard's rights go back to the old database, where it still runs.
Once the dashboard has run on `minerbot`, what it wrote is there only, so the move never goes back: running it again
finishes it. A run cut off anywhere is finished by running it again too. One cut off before `server.conf` moved starts
over, and drops the `minerbot` it made, which nothing has used. The bot keeps running throughout, on the connection
string it started with, until it's next recreated. Before PR 6 it didn't use MinerBot's database; since, it records its
status and history there, so a move while it runs loses at most what it records meanwhile in the old database.

Once the dashboard works on `minerbot`, the second step deletes the old database with cPanel's own tool, as the account
that owns it, so cPanel's map stays right:

```
bash /opt/minerbot/src/deploy/move-database.sh --drop-old
```

It refuses while `server.conf`, either env file or the dashboard's container still names the old database.
`state/database-moved` records the move and the deletion.

What changes for the owner afterwards: `minerbot` isn't in cPanel's MySQL Databases, the account's phpMyAdmin or cPanel's
account backups any more, which is the point. MinerBot's own nightly dump in `/srv/backups/minerbot` is its backup, kept
14 days on this server; copy it somewhere else as well, as with any backup. WHM's phpMyAdmin, as root, still shows it.

On a new server, MinerBot's database and the bot's login are made outside cPanel from the start, as root, with the
password on stdin: `CREATE DATABASE minerbot CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_520_ci`, then
`minerbot-bot` on both hosts with the rights above on `minerbot.*`, and `bot.env`'s `ConnectionStrings__BotConnection`
naming both.

## Taking the dashboard away

Once `./setup-dashboard.sh` has run, nginx loads MinerBot's file for every site, and that file depends on
`/etc/nginx/minerbot`. Take the dashboard away while the scripts are still installed, in this order:

1. `./setup-dashboard.sh --remove-nginx --yes`. Go on only once it says "MinerBot's file is out of nginx, and nginx
   reloaded." and `nginx -t` passes.
2. `rm -f /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog`, then `rm -r /etc/nginx/minerbot`. Never remove
   that folder while `/etc/nginx/conf.d/minerbot-dashboard.conf` exists: nginx would refuse every later reload,
   AutoSSL's included, and a restart of nginx or of the server would leave it down for every site.
3. `docker compose rm -s -f dashboard`, while `compose.yml` still names the service; then
   `docker volume rm minerbot-dashboard-keys` and
   `docker rmi minerbot-dashboard:current minerbot-dashboard:previous minerbot-dashboard:new minerbot-dashboard:failed`
   (a tag that isn't there only prints an error).
4. The dashboard's login, and the locked throwaway login the rights rehearsal makes (`minerbot-rights-check`), which a
   rights run cut off by a crash or a kill can leave behind, holding the rights file's grants:

   ```bash
   mariadb -e "DROP USER IF EXISTS 'minerbot-dashboard'@'localhost', 'minerbot-dashboard'@'127.0.0.1', 'minerbot-rights-check'@'localhost'"
   ```

   The dashboard's tables in MinerBot's database can stay.
5. `rm -f dashboard.env dashboard.env.previous state/dashboard-rights.applied.sql state/compose-ready-*.yml` and
   `rm -rf state/nginx-backup`: after a certificate change, the backups hold the old Origin key and proxy key. Then
   `rm -f /var/log/nginx/minerbot-dashboard.*`, which hold visitors' addresses.
6. In Cloudflare: the `bot` DNS record, the three rules and the Access application. In Discord: Reset Secret on the
   MinerBot Dashboard application, or delete the application.
7. Only then the move's undo above. Its `rm -f` removes `setup-dashboard.sh` and the nightly check, and its `cp -p`
   puts back a `README.txt` without the warning in step 2. The dumps in `/srv/backups/minerbot` stay, and no new
   ones are made.

## When a setting changes

| What changes | What to do |
|---|---|
| Owners, admins, Discord client ID or secret, Access on or off, a new AUD, a new certificate | `./setup-dashboard.sh`: give the new value, Enter or `keep` for the rest. It recreates the dashboard or reloads nginx only as needed. |
| An admin or owner joins or leaves while Access is on | Their email in Cloudflare's Access policy (Zero Trust › Access › Applications › MinerBot admin), and their Discord ID with `./setup-dashboard.sh`. Both. |
| The proxy key or the dashboard's database password is suspected leaked | `./setup-dashboard.sh --no-questions --rotate-proxy-key` (or `--rotate-db-password`) |
| The request-signing key is suspected leaked, or `./status.sh --check` says the copies differ | `./setup-dashboard.sh --no-questions --rotate-request-key`, or without `--rotate-request-key` to make the copies agree. It writes `bot.env`, then `dashboard.env`, and recreates the dashboard, then the bot ("The bot's requests") |
| A PR changed the nginx file (deploy.sh says so, once the dashboard of that commit runs) | `./setup-dashboard.sh --no-questions` (over SSH without a terminal, with the owner's go-ahead: `--no-questions --yes`) |
| WHM's piped logging goes off or on | Nothing: cPanel's hook keeps `pipelog.conf` in step (see nginx below) |
| The bot's token, webhook, Steam key or Steam user | `./fill-secrets.sh`, then `docker compose up -d --no-build --no-deps --force-recreate bot` |
| Anything else in bot.env | Edit it, with values only by the safe-character rule ("printable ASCII only; none of `$`, `"`, `'`, `\` and the backtick; no space at the start or the end; no `#` right after a space; spaces inside a value are fine"), then the same recreate |
| The database rights | A PR changes `deploy/dashboard-rights.sql`, and the next dashboard deploy applies it. Setup applies again only what the last apply used. |
| The database names | `/opt/minerbot/server.conf`, and the same names in `bot.env`'s two connection strings. Then `./setup-dashboard.sh --no-questions`, before any dashboard deploy, which would move the rights first: it rewrites `dashboard.env`'s connection strings, moves the login's rights, and may give it a new password. Then `docker compose up -d --no-build --no-deps --force-recreate bot`. A database renamed outside cPanel also needs the bot's login's grants moved. |

Settings are read only when a container is created: `docker restart` keeps the old values, so after editing a setting
by hand, run `docker compose up -d --no-build --no-deps --force-recreate <service>`, and always name the service. The
dashboard's socket, `Dashboard__UnixSocket`, comes from `compose.yml`, which wins over `dashboard.env`.

The certificate's end date: `setup-dashboard.sh` prints it, `README.txt` has it (rendered on install, and again by
setup when the certificate changes), `status.sh` shows the days left, and the nightly check mails 30 days ahead.

## The bot's requests

The dashboard leaves the bot signed requests in MinerBot's own database, in `bot_commands`, and the bot takes each one
within seconds and writes how it ended on the same row. The repository's README lists them ("Requests to the bot").
Since PR 10 the dashboard's pages send them: Bot controls (`/admin/bot/controls`), the cache clear and Announce now after
an armor review, a server's test message, refresh and leave, the blacklist's custom-link lookup, and the message about
each sign-in. `request.sh` sends one from the server too, for when the pages can't be reached.

### The request-signing key

- One key, 32 random bytes written as 64 hexadecimal digits in small letters, which both apps hold:
  `REQUEST_SIGNING_KEY` in `bot.env` and `Dashboard__RequestSigningKey` in `dashboard.env`. The dashboard signs each
  request with it, and the bot refuses one that doesn't verify. So there are four copies: the two files, and each
  container's environment, which Docker sets only when it creates the container.
- `setup-dashboard.sh` makes it with `openssl rand -hex 32` inside its unit, and never shows it. Its step 2b chooses: a
  new one for `--rotate-request-key`; else `bot.env`'s, when it's well formed and differs from `dashboard.env`'s, since
  setup writes `bot.env` first: a run cut off between the two writes leaves `bot.env`'s the newer, and a run that
  stopped because `bot.env` couldn't take a new key, as on a full disk, may have moved `dashboard.env` onto that key all
  the same, while the bot kept `bot.env`'s; else `dashboard.env`'s, when it's well formed; else a new one. A key that
  isn't well formed counts as none, and is never quoted.
- It writes `bot.env` (600, replaced by rename, every other line kept) before `dashboard.env` moves. After the
  dashboard's recreate, it recreates the bot when the bot's container runs with another key than `bot.env` holds, and
  waits until it's ready, as a deploy does: about 30 seconds offline, which the summary before "Go ahead?" says. A bot
  that isn't ready after its recreate has the key already, so the next run finds nothing to recreate: the problem line
  gives its log, and `docker compose up -d --no-build --no-deps --force-recreate bot` for once the cause is fixed. A
  stopped bot is left stopped, and loads the key when it's next created: `./status.sh --check` and `request.sh` then
  give that recreate, rather than setup. A dashboard that isn't ready after its recreate stops the run before the
  bot's, so the bot still runs with another key than `bot.env` holds: the stop line says it refuses the dashboard's
  requests until `./setup-dashboard.sh --no-questions` recreates it, as `./status.sh --check` and `request.sh` say too.
- Setup stops before anything changes when `bot.env` is missing: "bot.env is missing, so the request key has nowhere to
  go. Nothing was changed."
- Without a usable key each app runs as before, with requests off: the bot records `requests.off` once a run and
  refuses every request, saying why; the dashboard starts with an empty key and sends nothing, and stops at its start
  with a key that isn't 64 small hexadecimal digits, as its `check-settings` does, so a deploy refuses one before any
  swap.
- `./status.sh` shows one line for the key, and `./status.sh --check` reports a key missing or malformed in either
  file, the two files apart, and a container on another key than its file holds, each line saying what to run. The
  keys are only compared, never printed.
- To rotate it: `./setup-dashboard.sh --no-questions --rotate-request-key`, at a quiet hour. Setup recreates the
  dashboard with the new key before the bot, so in the seconds between the two recreates the bot, still on the old key,
  refuses what the dashboard signs with the new one: since PR 10, the message about a sign-in in those seconds, or a
  request sent from a page. The bot can't tell another key from a forged row, so its refusal and its direct message
  about a signature that didn't match, at most one an hour, name both causes. The Overview's banners show such a
  refusal for a day, and the Sessions page a refused sign-in message. One at the time of a rotation is the rotation's;
  at any other time, `./status.sh --check` says which. A bot restarted by a request keeps its container, so it never
  picks up a changed `bot.env`: only setup's recreate, or a deploy that swaps the bot, does.
- `fill-secrets.sh`, `move-database.sh` and the bootstrap leave `REQUEST_SIGNING_KEY` as it is and never print it:
  they change other lines of `bot.env` with `write_env`, which keeps every other line, or only read it. The backup
  never reads `bot.env`.

### PR 9's first deploy

1. `./deploy.sh <commit>`. It installs the deploy files, `request.sh` among them, migrates to version 4 (`bot_commands`
   and `bot_settings`), applies the rights, and swaps the dashboard (no key yet, so requests are off) and the bot (no
   key, so `requests.off`; no settings row, so posts aren't paused).
2. `./setup-dashboard.sh --no-questions`. It makes the key, writes `bot.env` then `dashboard.env`, and recreates the
   dashboard and then the bot.
3. `./status.sh --check` shows no line about the request key.

### request.sh

```bash
./request.sh <kind> [--guild <server ID>] [--channel <channel ID>] [--text <text>]
./request.sh --list
```

- It refuses unless it runs as root, `dashboard.env` is there, both files hold the same well-formed key and each
  container runs with its file's, `minerbot-dashboard:current` is there, and the bot runs. Each refusal says what to
  run, and "Nothing was sent." A `--guild` or `--channel` that isn't a Discord ID of 17 to 20 digits, or comes twice,
  is refused with this script's usage before anything else.
- It takes the lock and holds it until the bot has answered, so no deploy or setup runs meanwhile, and a restart never
  lands in a deploy's readiness check. While another run holds the lock, it refuses.
- It runs `minerbot-dashboard:current` once, as `minerbot-request`, removed with `docker rm -f -v` before and after,
  with `request <kind> [--guild ...] [--channel ...]`: `--network host` to reach MariaDB, without the image's health
  check, which would ask for a dashboard that doesn't run there, read-only, 256 MB and 64 processes, under
  `run_for 900`, which covers a 10-minute expiry, the bot's longest action, and a restart with the 6 minutes the
  command then waits. `--list` takes no lock, so it runs as `minerbot-request-list`, and never removes the one-off of a
  request that waits for the bot, which Docker would kill. Only `ConnectionStrings__BotConnection`,
  `Dashboard__RequestSigningKey` and `Dashboard__Owners` from `dashboard.env` go in, by name, and the text as
  `MINERBOT_REQUEST_TEXT`: never on a command line, and never `--env-file dashboard.env`, which would hand it every
  secret. The command uses the dashboard's own login, so the check also proves its rights, with a pool of 2
  connections, which stays within the login's 15 beside the running dashboard's two pools of 6.
- The request says the dashboard's first Owner asked, named "request.sh on the server", with no address. The command
  prints the request's number, then the bot's answer ("Done at ...: ..."), a lookup's account number or a test
  message's ID, and the bot's settings after a pause, a resume, a status text or a restart. After a restart, it waits up
  to 6 minutes for a new run of the bot to be up on Discord: the bot writes done first, then lets a notification run the
  schedule started meanwhile end, which takes at most 270 seconds, before it stops. Once the new run is up, it prints
  "The bot was back on Discord at T UTC, N s after the request: its new run started at R UTC.", T being the bot's own
  record of that run's first connection to Discord, or, while the bot hasn't written that record yet, "The bot was back
  on Discord by T UTC: its new run started at R UTC.", T being the first 5-second status sample that showed it
  connected, which says only by when. It reads that record once and never waits for it, and neither line changes the
  exit code or the history line. It never prints the key, the signature or a connection string. For `steam.lookup`,
  `--text` takes a custom link's name or the whole link.
- One history line: `request  <kind>  -  <state>  request <number>`, the state being `done`, `failed`, `refused`,
  `expired`, `unknown`, `no answer` or `not sent`.
- Exit code 0 done, and the bot back after a restart; 1 refused, failed, expired, unknown, no answer, or refused before
  sending; 2 the command refused an argument, with nothing sent.
- A dashboard image from before PR 9 doesn't know the command, which `request.sh` recognises: "The dashboard's image
  doesn't know the request command: deploy PR 9's dashboard first."

### PR 9's live check

Each request once on the real bot, over SSH, starting at least 5 minutes after setup recreated the bot, since the bot
takes no restart in its first 5 minutes, with the owner's go-ahead for the restart and the leave. The owner confirms
what shows in Discord: the direct messages, the status text and the silent test message.

| # | Command | Look for |
|---|---|---|
| 0 | `./status.sh --check` | no line about the request key |
| 1 | `./request.sh caches.clear` | done |
| 2 | `./request.sh job.notifications` | done, and a new run of the notification job on the Bot page |
| 3 | `./request.sh job.colors` | done, and a direct message naming `request.sh on the server` |
| 4 | `./request.sh steam.renew` | renewed or kept, and a direct message |
| 5 | `./request.sh steam.reconnect` | "logged on again after N s"; "Steam went offline", then "Logged on to Steam" on the Bot page; a direct message |
| 6 | `./request.sh steam.lookup --text <a known custom link>` | the account number `/more steam_id` gives |
| 7 | `./request.sh guild.test_message --guild <24kConnect> --channel <a channel>` | the silent embed, with no ping |
| 8 | `./request.sh guild.refresh --guild <24kConnect>` | done, with the channel count |
| 9 | `./request.sh owner.signin_alert` | the sign-in direct message, with no address |
| 10 | `./request.sh posts.pause --text "Live check of PR 9"` | done; a direct message with the reason; the settings printed as paused; the next notification run's detail on the Bot page says posts are paused |
| 11 | `./request.sh status.set --text "Live check of PR 9"` | the bot's profile shows it; a direct message |
| 12 | `./request.sh bot.restart` | a direct message; back on Discord within 2 minutes, or up to 6 when a notification run had just started, which the restart lets end; the settings still paused; the profile still shows the text; a "Posts paused" event on the Bot page, "still paused since ...", at the start |
| 13 | `./request.sh bot.restart` again, at once | refused by the limit: "The bot takes this at most once every 10 minutes, and last took one at ...". The limit comes before the 5 minutes since a start, whose refusal the tests prove |
| 14 | `./request.sh posts.resume` | done; a direct message; the next notification run posts what waited since step 12's restart, which lost what the paused runs found before it |
| 15 | `./request.sh status.set` | back to "Playing Murder Miners on my PC"; a direct message |
| 16 | `./request.sh guild.leave --guild <a throwaway server>` | the bot leaves; the servers page shows "left on dashboard request N"; a direct message |
| 17 | `./status.sh --check`, and the Bot page | the new events show by their labels, such as "Posts paused" and "Posts resumed"; "Message sent to the owner" rows show request numbers and no names |

A leave deletes that server's channel settings, as a removal does, and only a new invite undoes it, so step 16 needs a
server made for the check, with MinerBot invited before it. Without one, skip it: the tests cover the leave.

### PR 10's deploy

PR 10 adds the dashboard's pages that send requests, and the Sessions page. The bot doesn't change.

1. `./deploy.sh <commit> --only dashboard`, with the owner's go-ahead, at a quiet hour. It installs the deploy files, the
   rights file with its one new right among them, runs no migration, so it takes no dump, builds the dashboard, checks
   its settings (there's no new one, so `dashboard.env` stays as it is), applies the rights after their rehearsal, and
   swaps the dashboard. The bot isn't built or touched.
2. `./status.sh --check` is clean.

The new right is `SELECT` on `schema_version`, which the Sessions page reads to compare the migrations MinerBot's
database has run with the ones built into the dashboard. To undo the deploy after its swap completed, `./rollback.sh
dashboard` puts PR 9's dashboard back ("Undoing a deploy" says how to tell, and what to do otherwise); the right stays,
unused and harmless, as a deploy keeps a previous image's rights. PR 9's dashboard sends no message about a sign-in,
and the requests PR 10's pages left are taken or expire as usual. It can neither show nor undo a pause or a status text
set from PR 10's pages, which outlast the rollback ("What to know" below).

### PR 10's live check

In the owner's browser, through Cloudflare Access, with `./status.sh` over SSH, read-only, before and after. Steps 7 to
11 need a sign-in from the last 15 minutes, all but Resume posts and Back to the default status, and the page offers one
when it's older. What a press's answer is since PR 10b, and what still needs care, is under "What to know".

| # | In the browser | Look for |
|---|---|---|
| 0 | `./status.sh --check` | clean |
| 1 | Sign in at https://bot.murderminershub.com/admin, signing out first if the browser is still signed in from before the deploy: a session outlasts the swap, and only a new sign-in sends the message | within about 10 seconds, a direct message "Dashboard sign-in: ... signed in as Owner ..."; no banner saying requests are off |
| 2 | Sessions | this session, with its address and browser, marked as this one; each check OK or a note: the rights match with 15 connections and 20 seconds, the keys folder and the temporary folder writable, the structure up to date (migrations 1 to 4), Access on with fresh keys, requests on with no refusal, the last direct message sent (step 1's), the sign-in messages counted |
| 3 | Controls, within 15 minutes of step 1 | "Posts aren't paused."; "Discord shows the default status, ..."; each control's last use from PR 9's live check, by "request.sh on the server"; each one "Available now.". Resume isn't drawn while posts aren't paused, and a last use can be a refusal beside "Available now." |
| 4 | Clear the caches | Queued, then "Done at ...: Cleared the armor-name and latest-maps caches." within about 5 seconds |
| 5 | Servers, the 24kConnect server: Rewrite the copy, then Send a test message to the least visible channel the picker offers | the copy rewritten, with its count of text and announcement channels; then, to a channel the picker offers, which is one of the channels 24kConnect chose for MinerBot's posts with `/admin set_channels`, never one it didn't, so not #bot-testing unless that's one of them: done, with "Open the test message in Discord", which opens the silent "MinerBot test message" embed. It's a real embed, which that channel's members see and the bot can't delete: delete it by hand through that link. Don't change `/admin set_channels` for the test: the kinds left out of it are turned off |
| 6 | First confirm that [U:1:22202] isn't listed: search the Blacklist, or run `SELECT COUNT(*) FROM ihvph_blacklist WHERE steam_id = 22202` in the website's database, which gives 0. If it is listed, check only the lookup, and never press Remove. Then Blacklist, Add, with https://steamcommunity.com/id/gabelogannewell, the name "Live check" and the reason "PR 10 live check" | the page back with the boxes kept and the lookup's box saying "That link is account [U:1:22202]."; Add to the blacklist again makes entry [U:1:22202]; Remove, pressed straight after the add, takes it away (or give tomorrow as the end date); the audit log shows the lookup, the add and the removal. Since PR 10b, reloading the add's "already on the blacklist" answer after the removal adds nothing: it's a page of its own, not the form sent again |
| 7 | Restart, only as "A restart from the dashboard" below says, with the owner's yes on its costs | once the sign-in is 15 minutes old and the page is loaded again, the restart section offers a sign-in again, and Sign in again with Discord brings a second sign-in message and the page back (before then the form shows at once, and Your sign-in says until when); typing `restart` and pressing Restart the bot shows Queued, "Done at ...: The bot is restarting. It's usually back within a minute.", then "Back on Discord at T, N s after the request: its new run started at R." within 2 minutes, or up to 6 when a notification run had just started, which the restart lets end, with "Back on Discord by T: ..." instead until the bot has written its record of the new run's connection, and "The owner was sent a direct message about it."; a direct message "Restart the bot, asked for on the MinerBot dashboard by ..."; Stopping and Started on the Bot page. A refusal, a failure, an expiry, "Not sure it happened", the dashboard's own "can't tell whether it was sent", "Not sure it was saved" or "Too many requests", a request still queued at the mark, or no request at all ends the step, with no second press, whatever the page says about sending it again: if nothing restarted, tell the owner approvals can resume, and to try again, start a new cycle before a later mark |
| 8 | Only when step 7 is back, as "A restart from the dashboard" says: load `/admin/bot/controls` by a fresh GET at its bare address, and go on only with "The bot takes this again after X UTC." under Restart and "these work until Y" under Your sign-in ("Available now.", a waiting line or no line there means skip it: the press would really restart the bot). Read the server's time with `date -u`, then press Restart the bot again, typing `restart` without Enter and clicking once, before the earliest of the new run's start + 4:00, X - 1:00 and Y - 0:30 | "The bot takes this at most once every 10 minutes, and last took one at ..., so nothing was sent. Try again after ...", with no new request or audit row; nginx's access log shows the POST's 302 and the GET's 409 (copy their time, method, path and status only). The answer's address ends in `?notice=` and 32 hexadecimal digits, and Reload and Back on it send nothing. If nginx's own "The MinerBot dashboard is restarting or stopped. Try again in a minute." answers the press instead, the step ends there: never reload that page or go back to it, which sends the press again and restarts the bot once the limit has passed; close the tab, and read Restart's last use on the page opened anew. The dashboard's own error page and "Too many requests" end the step too, whatever they say about sending it again: nothing was sent, and the form sent again once the limit has passed restarts the bot. Never sign in again for this step: a new sign-in opens 15 minutes in which a press past the 10-minute limit restarts the bot. On a skip, clear the typed box and close the tab |
| 9 | Pause posts with a reason, typing `pause`; once its box says to reload the page, reload it, then Resume posts at once | each done, with its direct message and "Reload the page to see the new settings."; the Overview's paused banner comes and goes. A pause holds the posts of every server and outlasts restarts and rollbacks, and Resume needs no fresh sign-in: the check isn't over until Controls says "Posts aren't paused." |
| 10 | Optional: set a status text, then Back to the default status at once | Discord shows each. A status text shows in every server and outlasts restarts and rollbacks, and Back to the default needs no fresh sign-in: the check isn't over until Controls says "Discord shows the default status, ..." |
| 11 | Optional: sign in from a second browser or a phone, then on the first, Sessions, End it | in the second browser, a link or a reload goes on to Discord's Authorize screen, where not authorizing is fine, a button that changes something or asks the bot, such as Clear the caches, lands on "Your session ended", Sign out goes to the home page, and a search or a filter seems to do nothing ("What to know"); Changes to sessions shows "Ended a session" |
| 12 | Audit log | each request reads "Sent to the bot", with a "Bot request N" link to its page and the bot's answer there |
| 13 | Optional, only if a pending armor that was never approved should be approved, and only once the Bot page shows a notification run that started after step 7's Started event, not one the restart let end, since the bot's first run after a start only notes the approved armor it finds, and armor approved before that run is never posted: Approve it | the page says the bot was asked to clear its armor-name cache, and its box says done; Announce now posts the armor within seconds |
| 14 | Leave | skipped unless there's a throwaway server, as in PR 9's |
| 15 | `./status.sh --check`, Controls, and the logs since the new run's start | clean, though it reads neither the pause nor the status text, so it proves neither undo: Controls says "Posts aren't paused." and "Discord shows the default status, ...", or `mariadb <BOT_DB from server.conf> -e "SELECT posts_paused, status_text IS NULL FROM bot_settings WHERE id = 1"` gives 0 and 1; and each of the counts below is 0 |

Step 15's counts, from the new run's start, which `docker inspect -f '{{.State.StartedAt}}' minerbot-bot` gives:

```bash
docker logs --since <start> minerbot-bot 2>&1 | grep -c -E '^(warn|fail|crit): |\[General/(Warning|Error|Critical)\]'
docker logs --since <start> minerbot-dashboard 2>&1 | grep -c -E '^(warn|fail|crit): '
docker logs --since <start> minerbot-bot 2>&1 | grep -ci 'unhandled exception'
docker logs --since <start> minerbot-dashboard 2>&1 | grep -ci 'unhandled exception'
```

The old run's clean stop logs a warn from RequestWorker, a crit from Worker and a `[General/Warning]` Gateway line just
before that start. They aren't faults, and `--since` leaves them out.

### A restart from the dashboard

A restart costs posts, so the dashboard's Restart the bot is pressed only like this, which is how PR 10's live check
restarted the bot. The bot's jobs, cron and nginx's log go by the server's Eastern time; the dashboard's and the
database's times are UTC, as `date -u` gives the server's.

1. **The owner's yes, on these costs:**
   - New and updated maps, approved armor and new lobbies that come between the notification job's last read and the
     new run's first run are never announced: the bot's first run after a start only notes what it finds.
   - An update of a map posted since the bot's last start, still in its 24-hour cooldown, is lost, and the cooldown
     forgotten.
   - Everyone else who approves armor holds off too: the admins, and approvals on the website.
   - The bot is off Discord about 2 seconds, and commands used then get no answer.
   - A start renews the Steam token once.
   - A lobby can be announced again.
   - Rarely the bot doesn't come back (a deleted webhook, or Discord's API failing at the start), and it stays down
     until the owner approves a fix, so the owner stays reachable.
2. **Not at these times, Eastern:** 02:28 to 02:36, when cPanel's nightly update restarts MariaDB and Docker at about
   02:31; 04:20 to 04:31, the members count and the clean-up; 05:45 to 05:58, the nightly `./status.sh --check`; and
   11:58 to 12:02, the Steam token's renewal. In UTC that's 07:28 to 07:36, 09:20 to 09:31, 10:45 to 10:58 and 16:58 to
   17:02 in winter, and an hour earlier in summer.
3. **Before:** `./status.sh` shows the bot healthy, and its restart count is noted; nothing is waiting, and posts aren't
   paused; no deploy or rollback runs; and no restart was claimed in the last 10 minutes.
4. **Sign in fresh at A,** the time of the audit log's sign-in row. M is the first five-minute mark with M - 110 s at or
   after A + 5:00. A restart is sent only within 15 minutes of the sign-in and once the last one is 10 minutes old, so
   with this one claimed at A + 5:00 or later, no later press in that session can restart the bot again. In a live
   check, if A + 14:30 leaves no room for its step 8, sign in again before pressing anything.
5. **Between M - 150 s and M - 110 s, press Run the notification job now.** Go on only with its box Done, "The
   notification job ran in N s." (not still running), Steam logged on, and no new failure line in the bot's log.
6. **Press Restart the bot once,** typing `restart` without Enter, by M - 45 s and never before A + 5:00. If that moment
   has passed, press nothing: let M's run end, and start again at 5.
7. **Back** means all of these: the restart's row in `bot_commands` is done, with `claimed_at` set; `bot_status` is on
   another `run_id`, connected, with `stopped_at` NULL; Steam is logged on; and the bot is healthy, with exactly one more
   restart and a steady StartedAt. Read them from the database and with `docker inspect -f`, never a plain `docker
   inspect`, which prints the environment, and never from a box in a hidden tab, which isn't refreshed while hidden.
8. **If it goes wrong, never press a second time:**
   - Refused, failed, expired, "Not sure it happened", the dashboard's own "can't tell whether it was sent", "Not sure
     it was saved" or "Too many requests", nginx's own "restarting or stopped" page, or no request at all: the cycle
     ends there, whatever the page says about sending it again. The bot's refusal while a notification run is under
     way says to try again in a minute, and so does nginx's page, whose reload sends the press again, but a press a
     minute later lands just after the mark's run, and the new run's first run only notes what it finds, which loses
     about 4 more minutes of posts. If nothing restarted, tell the owner approvals can resume; to try again, start a
     new cycle at 4, before a later mark.
   - Still queued at M: press nothing, and keep approvals held until it expires or the new run's first run.
   - Two or more restarts, or not back within 6 minutes: press nothing. Read `docker inspect -f` and the counts, and ask
     the owner for one named command: `docker start` if it exited, `docker restart` if it hangs, `docker stop` to
     contain a loop. For a deleted webhook, `./fill-secrets.sh` and the recreate it prints.
   - Never `./rollback.sh bot` or a deploy: the previous image makes the same webhook call.
   - Discord back but Steam not logged on 2 minutes after the start: tell the owner, and don't restart again.
9. **Approvals resume** once the new run's first notification run is ok, with a NULL detail and no failure line: its
   row in `job_runs` has the new `run_id` and started after the Started event.
10. **The words:** Queued, then "Done at ...: The bot is restarting. It's usually back within a minute.", then "Back on
    Discord at T, N s after the request: its new run started at R." once the bot has written its record of the new
    run's connection, or until then "Back on Discord by T: its new run started at R.", and "The owner was sent a direct
    message about it." PR 10's live check saw "9 s", which was the bot's next 5-second status sample: the bot was
    connected 4.6 s after the press.

### PR 10b's deploy

PR 10b fixes what PR 10's live check found, in the dashboard alone: a press's answer has an address of its own, so a
reload or Back on it doesn't send the form again ("What to know" says what still needs care); Check again loads the
page again; a restart's box says when the bot was back by the bot's own record of its connection, and so does
`request.sh`; and a press that would change something or ask the bot, sent after the session ended, says so. The bot
doesn't change.

1. Close every dashboard tab from before the deploy, and ask the admins to close theirs and to say no whenever the
   browser asks to send a form again: one may hold an answer that a reload would send again, and a refused press sent
   again acts once what refused it has gone.
2. `./deploy.sh <commit> --only dashboard`, with the owner's go-ahead, at a quiet hour. It installs the deploy files
   (only this README and the tests changed, and neither is installed), runs no migration, so it takes no dump, builds
   the dashboard, checks its settings (there's no new one, so `dashboard.env` stays as it is), applies the rights,
   which haven't changed, after their rehearsal, and swaps the dashboard. The bot isn't built or touched.
3. `./status.sh --check` is clean.

Beyond the deploy's go-ahead, there's nothing for the owner to approve. Undo it as "Undoing a deploy" says. PR 10's
dashboard then answers a refused press, a failed one and one over the rate limit on the POST itself again, and ignores
`?notice=` and the notice cookie, which ends within 30 minutes. Until PR 10b is deployed again, what "What to know"
says of a press's answer doesn't hold: a reload, Back or a restored tab on any of those answers, the error page and
"Too many requests" included, sends the form again, so open the page anew instead; Check again under a request's box
only scrolls, so reload that page to look again; and a press after the session ended seems to do nothing, so reload
the page. As before PR 10b, a restart's box takes T in "Back on Discord at T" from the first 5-second status sample
that showed the bot connected, a few seconds late, and never says "by", and `request.sh` prints "The bot is back on
Discord N s after the request: ...".

### PR 10b's live check

In the owner's browser, through Cloudflare Access, with `./status.sh --check` clean before and after. It costs nothing:
the requests it sends are its cache clears and the usual message about each sign-in it needs.

| # | In the browser | Look for |
|---|---|---|
| 1 | Controls: Clear the caches, then Check again under its box | the address gains `again=1`, nginx's access log shows a GET, and the box is drawn as it stands: once final, without Check again. If it isn't final yet, its Check again takes `again=1` off and loads the page again. The bot looks every 3 seconds, so now and then it has ended the clear before the page first shows its box, which is then final, with no Check again: press Clear the caches once more |
| 2 | With a sign-in from the last 15 minutes, under Pause posts, the reason "Live check of PR 10b" and the word `paws`, then Pause posts | the address ends in `?notice=` and 32 hexadecimal digits; the alert reads "Type pause in the box to confirm. Nothing was sent."; the reason is back in its box, and the word's box is empty; nginx logs a POST 302, then a GET 400. Reload: the same page, with no question about sending the form again, and no request or audit row. Back: the controls page as it was before the press |
| 3 | Sign out in a second tab, then press Clear the caches in the first | "Your session ended"; nginx logs a POST 303, then a GET 200. Its link, Sign in again with Discord, signs in, with one sign-in message, and returns to `/admin/bot/controls`. The audit log, read then, has nothing for the press: after the sign-out's "Signed out" come only that sign-in's "Signed in" and "Asked the bot to tell the owner about a sign-in", the request for its message |
| 4 | Nothing to press | the restart's new words need no restart here: the tests cover them. The next real restart's box should say "Back on Discord at ..." with the time of the bot's `discord.connected` event for its new run |

### What to know

- **One bot per database.** The bot counts a limit from `bot_commands`, then takes the request in a second statement,
  so two bot processes on one database, such as a developer's run against the live one, could each take one request
  past a limit, and a restart may stop the other process. Each request is still acted on once.
- **A bot rolled back** to an image from before PR 9 doesn't read `bot_settings`, so it posts while posts are paused,
  and leaves the requests to expire.
- **A deploy that finds the bot already on its image** only checks that it's ready again, without recreating it. A
  restart sent from the dashboard's page during that check moves the bot's restart count, which fails the deploy.
  Nothing is put back, since the image is the same, and the next deploy goes through: rare, and harmless. `request.sh`
  holds the lock, so it can't do this.
- **A sign-in that can't leave its message to the owner doesn't happen.** The request for the message is written in
  the sign-in's own transaction, so while the dashboard's login can't add a row to `bot_commands`, every sign-in ends
  on the page that says the database can't be reached, though `./status.sh` shows MinerBot's database up. The
  dashboard's log names MariaDB's error (`docker logs --tail 50 minerbot-dashboard`): 1142 or 1143 is a right the login
  lacks, and `./setup-dashboard.sh --no-questions` applies again the rights the last apply used.
- The nightly clean-up deletes a request 90 days after it was made, or after the bot ended it if that came first, so
  a row dated years ahead by a writer without the key, which the bot refuses, still goes. The dumps hold the requests,
  so a restore brings them back as they were dumped, which only each one's expiry keeps from being acted on.
- **What a restart leaves:** the Stopping and Started events, and the owner's direct message; the channel copy
  rewritten and the commands registered again, as at every start; on the Bot page's uptime, the restart's minute as a
  whole minute down on Discord and Steam, since the new run's first sample replaces the old run's for that minute, until
  a fix to the bot (PR 11); `./status.sh` showing the bot with "1 restarts" until a deploy that swaps the bot,
  `./rollback.sh bot` or setup's recreate makes a new container, which isn't a fault, as `--check` doesn't read it;
  possibly a lobby announced again or one missed, which nothing records; and the maps' cooldown forgotten.
- **A pause or a status text outlasts a rollback,** and every restart, since both are kept in `bot_settings`, and a
  dashboard from before PR 10 can neither show nor undo either. After any rollback, read `mariadb <BOT_DB from
  server.conf> -e "SELECT posts_paused, status_text IS NULL FROM bot_settings WHERE id = 1"`: 0 and 1, or no row, mean
  posts aren't paused and Discord shows the default status. Otherwise, unless the owner wants it kept, and with their
  yes: `./request.sh posts.resume`, or `./request.sh status.set` with no text. Never restart or redeploy the bot while
  posts are paused: that loses what the pause holds.
- **A press's answer has an address of its own** since PR 10b. Controls, a server's page and its leave, the armor
  review and the blacklist's add page answer a press with a redirect, but for a form that had expired, was incomplete
  or names something that's gone, which gets the same answer however often it's sent: a press that was sent or done
  goes on to a page that shows it, as before, and one that was refused, or whose save wasn't confirmed, to the page's
  own address with `?notice=` and 32 hexadecimal digits. Reload, Back and a restored tab fetch that page again and send
  nothing. Its message is kept for 30 minutes, in that browser's session, in an encrypted cookie, never in the
  address. There's one at a time: a later press, in any tab, answered at such an address, a blacklist lookup included,
  replaces it. After its 30 minutes, after a sign-in again, or once it's replaced, the address shows the page as it is
  now, with the line "The message this address showed after a form isn't kept any more, so this is the page as it is
  now." Check again loads the page again: its address gains or loses `again=1`. A press that
  would change something or ask the bot, after the session ended, lands on "Your session ended", which says nothing was
  changed or sent and that what was typed isn't kept, and whose link signs in again and comes back to the page. A press
  that fails is redirected to the error page, which says nothing was changed and to go back to the page it was sent
  from and send it again there, and one over the rate limit to its "Too many requests", which within the same minute
  is only "Too many requests just now. Wait a minute, then try again.": a reload of either sends nothing. Going back
  matters after a request whose save wasn't confirmed: the page it was sent from keeps the form's key, which finds that
  request if it landed, where a page opened anew draws a new key, which would send a second. What still needs care:
  - a tab left open from before PR 10b's deploy, which may hold an answer that a reload sends again, and a refused press
    sent again acts once what refused it has gone, even in a later session: close it, as the admins should theirs;
  - the Sessions page's End and End every other session, a blacklist entry's Save and Remove from the blacklist, and the
    blacklist's Remove for a row with no Steam account number, which still draw a refusal, or a save that wasn't
    confirmed, on the POST, as before PR 10b: Back to such an answer or a reload of it may bring the browser's question
    about sending the form again, and sending it acts as a new press would, though on the blacklist only while the
    entry, or the row, is still as it was when the form was drawn. Say no, and open the page anew;
  - the dashboard's Cloudflare Access check answering a press in plain text, "Cloudflare Access couldn't be checked
    just now, ..." or "... this request didn't come through it. ...", which a reload sends again: open the page anew;
  - nginx's own plain "The MinerBot dashboard is restarting or stopped. Try again in a minute." answering a press,
    while the dashboard is stopped or restarting, as during a deploy's swap, or hasn't answered within 60 seconds,
    or an error page of Cloudflare's own: the press may have been sent, refused or never read, and a reload of that
    page, or Back to it, sends it again. Close the tab, and on the page opened anew read the control's last use or
    the audit log before pressing again; never press Restart again a minute later, as "A restart from the dashboard"
    says;
  - an ended Cloudflare Access session, after its 24 hours, which makes a press seem to do nothing: reload the page;
  - a search or a filter sent after the session ended, on the armor list, the blacklist, the audit log or the Bot
    page's events, which in Chrome still seems to do nothing, since the browser stops a form's redirect on to Discord:
    reload the page, which goes on to the sign-in.

  Before sending anything again after an answer that isn't clear, read the control's last use or the audit log. While
  the database stays down, a save it didn't confirm shows "Not sure it was saved", and a press that failed gets an
  error page whose only advice is "Try again in a few minutes.": reload it once the database answers, and it then says
  to go back to the page the form was sent from and send it again there, rather than open that page anew. Keep to one
  or two tabs: each address gets 60 requests a minute, the boxes' refreshes included.

## Rules for every script

- `set -Eeuo pipefail` and `umask 077`. `common.sh` holds constants and functions only, so sourcing it runs no command;
  every script sources the copy in its own folder. The one exception to the umask is `deploy.sh`'s checkout of `src`,
  under `umask 022` in a subshell: `src` holds only the public repository's files, inside `/opt/minerbot`, and a build
  keeps the files' modes, so a commit whose Dockerfile doesn't make what it publishes readable still works.
- Every docker, mariadb, mariadb-dump, curl, git, openssl, nginx, ss, sendmail, systemctl, systemd-run,
  systemd-tmpfiles, pgrep and ps call, every `bash -c`, and cPanel's `/scripts/restartsrv_nginx`, goes through
  `run_for [-k <grace>] <seconds>`, which is `timeout` with a 10-second grace. None of those names may stand where a
  command starts outside `run_for` itself, after `if`, `elif`, `while`, `until`, `exec`, `time`, `command`, `env`,
  `nohup`, `xargs` (`time`, `env` and `xargs` with their options too), an assignment, a backtick or a case item
  included; the tests check it.
- Every `read` has `-t`, and every `stty` runs only when `on_terminal` (`[ -t 0 ]`): without a terminal an unguarded
  `stty` fails, and in an EXIT trap under `set -e` it turns a finished run's status into 1.
- A named helper container (`minerbot-migrate`, `minerbot-dashboard-check`, `minerbot-request`,
  `minerbot-request-list`) is removed with `docker rm -f -v` before it starts and after it ends, since a run killed at
  its limit can leave one behind. The `-v` also removes the anonymous volume the dashboard image's `VOLUME /app/keys`
  gives the migration's container. Removing one that runs kills it, so two runs that can overlap never share a name.
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

## The dashboard's socket

On the server the dashboard listens on no TCP port, only on the Unix socket `/run/minerbot-dashboard/dashboard.sock`,
and nginx's two locations pass to it (`proxy_pass http://unix:/run/minerbot-dashboard/dashboard.sock;`). Any account on
the server, a hacked WordPress site say, could take a free port while the dashboard restarts or before it starts after
a reboot, and nginx would hand it visitors' requests, session cookies, Access tokens and the proxy key. No other
account can make a file in the socket's folder.

- The folder is `/run/minerbot-dashboard`, mode 2750, owner 1654 (the dashboard's user), group `nobody`, which nginx's
  workers run as: only the dashboard can make a file there, only processes in the group `nobody` can reach in, nginx's
  workers and cPanel's Apache among them, and the setgid bit gives the socket the group `nobody`. `/run` is emptied at
  every boot, so systemd-tmpfiles makes the folder again before Docker starts, from
  `/etc/tmpfiles.d/minerbot-dashboard.conf`. Every deploy and the bootstrap run
  `systemd-tmpfiles --create` on that file too, which puts a wrong mode or owner right.
- `compose.yml` sets `Dashboard__UnixSocket: /run/minerbot-dashboard/dashboard.sock` in the dashboard's `environment:`,
  and bind-mounts the folder at the same path with `create_host_path: false`, so a missing folder stops the start
  rather than Docker making one as root, where the dashboard couldn't write. The settings check that a deploy and
  setup run gets the same setting.
- Before it listens, the dashboard deletes a socket a killed dashboard left there, but only a socket, and only when
  nothing accepts a connection on it within 2 seconds: "Deleted the socket at ..., which a dashboard that was stopped
  without closing it left behind". It leaves a socket something still listens on, and doesn't start: "Something still
  listens on the socket at ..., so it's left as it is, and this dashboard can't listen there". A file or folder there
  that isn't a socket stops it with exit 1: "Dashboard:UnixSocket names a file or folder that isn't a socket, so the
  dashboard can't listen there. It never deletes one, so point the setting at a path that's free." `check-settings` and
  a start also refuse a value that isn't a full path of at most 100 bytes without spaces. Once it listens, it makes the
  socket 660, and a clean stop deletes it.
- A connection on the socket counts as 127.0.0.1: the visitor's address and https from nginx's headers are trusted one
  hop, as from loopback before, and the rate limits count per visitor. A request sent to the socket directly, without
  `X-Forwarded-For`, shows 127.0.0.1 in the logs and audit rows. Anything that runs as `nobody`, cPanel's Apache among
  them, can still connect to the socket, though it can't make a file in the folder, so the proxy key stays the second
  layer: without it the dashboard answers 404.
- The image's health check, `dotnet MinerBot.Dashboard.dll healthcheck`, asks `/health/live` through the socket.
- The readiness check and `status.sh` look at the folder (2750, owner 1654, group `nobody`) and the socket (a socket of
  1654 and `nobody` that the group can write to), with `socket_problem` in `common.sh`. The readiness check then asks
  `curl -s --max-time 5 --unix-socket /run/minerbot-dashboard/dashboard.sock -H 'Host: localhost' http://localhost/health/ready`,
  and then the home page, with `home_page_status` in `common.sh`: `-H 'Host: bot.murderminershub.com'`,
  `-H 'X-Forwarded-Proto: https'` and `http://localhost/` on the same socket, and the proxy key on curl's standard input
  through `-H @-`, so it's never in a command line. The key is the one the container runs with, from
  `docker container inspect`, or `dashboard.env`'s when Docker doesn't say, as `status.sh` takes it too. A 404 there
  means the key didn't match.
  By hand, `stat -c '%a %u %G %F' /run/minerbot-dashboard /run/minerbot-dashboard/dashboard.sock` prints
  `2750 1654 nobody directory` and `660 1654 nobody socket` while the dashboard runs.
- While the socket is missing, or there with nothing listening, nginx answers with its own plain 503 and
  `Retry-After: 30`.
- Local runs keep TCP: without `Dashboard__UnixSocket`, the dashboard listens on `127.0.0.1:5080`, or where
  `Dashboard__ListenAddress` and `Dashboard__Port` say (the root README).

## nginx

- `minerbot-dashboard.conf` uses the account blocks' own listen lines: `listen 80;`, `listen [::]:80;`, `listen 443 ssl;`,
  `listen [::]:443 ssl;` and `http2 off;`. Never `default_server` or `reuseport`: cPanel's `default.conf` has both on
  these ports, and nginx refuses to load when either is repeated.
- Its includes of cPanel's Cloudflare list, of `proxy-key.conf` and of `pipelog.conf` are patterns
  (`cloudflare.con[f]`), so a missing file makes nginx load without it instead of failing `nginx -t` for every site.
  Without the Cloudflare list every visitor here is refused, and without the proxy key the dashboard answers 404.
- It has no secret in it. The proxy key is one line in `/etc/nginx/minerbot/proxy-key.conf`, which only root can open.
- **pipelog.** cPanel's pipelog module, which it loads while WHM's piped logging is on, sends every request line, query
  strings included, to the domain logs. The file never names `pipelog` itself, which nginx refuses for every site when
  the module isn't loaded. Both blocks include `/etc/nginx/minerbot/pipelog.con[f]`, which holds `pipelog off;` while
  `/etc/nginx/conf.d/modules/ngx_http_pipelog_module.conf` exists, and nothing otherwise. `nginx_change` writes it, and
  installs cPanel's hook `/etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog` (root, 755, a `/bin/sh` script),
  which cPanel runs in every `ea-nginx config --all` or `--global`: after it writes or deletes the module's file, and
  before its own `nginx -t` and reload. So WHM's piped logging can go off or on without nginx refusing MinerBot's file.
  The hook does nothing once `/etc/nginx/minerbot` is gone. Without cPanel's folder for it, setup says: "Note: cPanel's
  folder for nginx's config scripts, /etc/nginx/ea-nginx/config-scripts/global, isn't on this server, so pipelog.conf
  can't follow WHM's piped logging. Keep piped logging on: with it off, nginx refuses MinerBot's file, and every site
  with it." If `nginx -t` ever says `unknown directive "pipelog" in /etc/nginx/minerbot/pipelog.conf`, pipelog.conf is
  stale: run the hook, then `/scripts/restartsrv_nginx --reload`. Setup counts a `pipelog.conf` or a hook that isn't
  what it would write, a missing one included, as a change of nginx's files, so `./setup-dashboard.sh --no-questions`
  writes both again while `nginx -t` passes.
- Only `setup-dashboard.sh` installs it, through `nginx_change` in `common.sh`:
  1. The marker `state/nginx-change` and a backup of every file it may change, the four files, `pipelog.conf` and the
     hook, on disk before the backup is marked complete. INT, TERM and HUP are ignored until the marker is gone.
  2. The candidate passes `nginx -t` on its own with the server's main context (its `user`, `load_module` and
     `include` lines, from `main_context`) and an absolute `-c` path, before it goes near `conf.d`.
  3. The staged files are synced to disk, then installed by rename. Our `conf.d` file goes out first and comes back
     last, so a run cut off in between, by a crash or a power loss too, never leaves nginx a live `conf.d` file beside
     a certificate and key that don't belong together, or beside files it names that aren't there yet. A cPanel reload
     in those milliseconds loads nginx without the dashboard until the next reload.
  4. `nginx -t` with every site. A failure puts every file back byte for byte, and nginx isn't reloaded.
  5. cPanel's reload, which nginx must take. cPanel's script exits 0 whatever nginx's master does then, and a master
     that can't apply the new files, as when a listen address is held by another program, which `nginx -t` never
     tries, keeps its old workers and says why only in its error log. So a worker process that wasn't there before
     must start within 10 seconds (`pgrep -P <the master in /var/run/nginx.pid> -f '^nginx: worker process'`).
     Otherwise: "cPanel's reload exited 0, but nginx kept its old worker processes for 10 seconds, so it didn't load
     the files on disk: ..." and "nginx's reason: grep -F '[emerg]' /var/log/nginx/error.log | tail -n 5". A reload
     that failed or wasn't taken puts every file back and reloads nginx with them, in case the first reload loaded the
     new ones after all. If that reload fails or isn't taken either, the marker stays, with a WARNING, and the next
     setup run puts the old files back and reloads first. Without the PID file or workers to list, only cPanel's exit
     code counts, and setup says so.
  6. A smoke check: a request from this server, which isn't Cloudflare's, must get 403, and nginx must serve the
     installed certificate for the name. Otherwise setup reports a problem that names only what failed ("nginx runs
     the new file, but it answered N to a request from this server, where 403 was expected", or "... the certificate
     it serves for bot.murderminershub.com isn't the installed one"), with how to find another block naming the host.

  A change cut off in between leaves the marker, and the next setup run undoes it first, from its backup, and removes
  any copy a cut-off restore left. A restore, like the change, puts our `conf.d` file back last, after `pipelog.conf`
  is in step with cPanel's module again. `--remove-nginx`, the undo, the put-back and setup's lone reload, for an nginx
  that serves an older copy of the certificate, prove their reloads the same way.
- Never add `brotli off;` or another directive for a module the server doesn't load: `nginx -t` would fail and block
  reloads for every site. The read-only look on 5 November 2026 found that cPanel's http level sets nothing the
  dashboard's blocks need to undo.
- Never remove `/etc/nginx/minerbot` while `/etc/nginx/conf.d/minerbot-dashboard.conf` exists: nginx would refuse every
  later reload, AutoSSL's included, and a restart of nginx or of the server would leave it down for every site.
  `./setup-dashboard.sh --remove-nginx` first ("Taking the dashboard away").
- Never share raw `nginx -T` output: it prints root-only files, MinerBot's proxy key and cPanel's own proxy secret among
  them. Use `./status.sh --nginx-look`. It prints only the files `nginx -T` names that anyone may read, world-readable
  in folders anyone may enter, by their whole path, spaces included, never anything in `/etc/nginx/minerbot`, and no
  line naming `X-MinerBot-Proxy-Key` or `cPanel-localhost`; whatever it can't match to such a file, it leaves out.
- The logs. `/var/log/nginx/minerbot-dashboard.access.log` has the visitor's address, the time, the method, the path
  without its query string, the status, the size, the time taken, the dashboard's status and Cloudflare's ray ID: no
  referer, user agent or cookies. `/var/log/nginx/minerbot-dashboard.error.log` (level `warn`) gets a line when nginx
  can't reach the dashboard or a request goes wrong, with the visitor's address, the whole request line, query string
  included, and the referrer. Discord's callback, whose query string holds the sign-in's code, logs only emergencies,
  since nginx logs a missing socket, or one it can't open, as critical, with the request line. cPanel's logrotate rule
  for `/var/log/nginx/*.log` rotates both daily and keeps 21, but skips a day without lines, so a quiet log keeps its
  lines longer than 21 days, the error log for months.

## The database rights

`dashboard-rights.sql` says everything the dashboard's login, `minerbot-dashboard` on `localhost` and `127.0.0.1`, may
do. It's declarative: the login loses every right, then gets exactly these. Table and column rights only, and only what
today's code uses.

- Each deploy that swaps the dashboard applies the installed file, plus the GRANT statements below, through
  `apply_rights`: a rehearsal on a locked throwaway login (`minerbot-rights-check`), then the real login, then a
  comparison of the two through `rights-list.sql`, which never uses `SHOW GRANTS` (it prints the password's hash), and
  which ends a right held with the grant option in `grantable` and a role held with its admin option in `admin`. A
  failed rehearsal leaves the real login as it was; a failed apply leaves it fewer rights, never more; a difference
  means a right `REVOKE` can't take away, such as a PROXY grant, which a person must remove.
- Once the comparison passes, `apply_rights` keeps a copy of the file it applied as
  `/opt/minerbot/state/dashboard-rights.applied.sql` (600). Every setup run applies that copy again, never the
  installed file, so setup never gives the running dashboard rights no dashboard deploy applied: only a dashboard
  deploy moves the rights forward. Only before any apply has kept a copy, on the first setup, does setup apply the
  installed `dashboard-rights.sql`. A failed apply leaves the copy as it was.
- A `--only bot` deploy, a `--no-start` one, or one that stops before the rights, installs a newer
  `dashboard-rights.sql` without applying it, and the setup runs after it apply the copy, so the rights stay as the last
  dashboard deploy left them: the running dashboard doesn't use the new tables, and a newer rights file could take away
  a right it still uses.
- A dashboard older than the installed deploy files, as `./deploy.sh <older commit>` deploys it, gets the rights the
  last apply used instead (the copy, or the installed file before any apply kept one), and each GRANT statement of its
  own commit's `dashboard-rights.sql` they lack, which the log lists: "The deploy files stay those of ... (above), so
  the dashboard of ... gets the rights the last apply used, from state/dashboard-rights.applied.sql, and these GRANT
  statements of its own dashboard-rights.sql, which they lack and its code may still use:".
- Every dashboard a failed swap or `./rollback.sh dashboard` can put back before the next apply keeps what it uses
  too: the one that runs, and the ones `:current` and `:previous` name, each keep every GRANT statement of their own
  commit's `dashboard-rights.sql` that the rights lack, which the log lists: "The dashboard of <12>, which a failed
  swap or ./rollback.sh dashboard can put back, keeps these GRANT statements of its own dashboard-rights.sql, which the
  installed dashboard-rights.sql lacks:". So a right a pull request removed goes only once no such dashboard has it in
  its own file, a deploy or two after that pull request's. When `src/` doesn't have such a commit, the log says so
  with a warning, since that dashboard may then lack a right its code uses if it's put back. The file applied,
  `state/tmp/deploy.rights.<random>/dashboard-rights.with-<12>.sql`, with one `-<12>` more for each other commit that
  added GRANT statements, becomes the copy setup applies.
- A pull request whose code needs another right adds it in the same pull request, after the migration that makes its
  table (MariaDB refuses a right on a table that doesn't exist yet), and changes `DashboardRightsTests`. A right is
  removed only in a later pull request than the one whose code stopped using it, so `rollback.sh`'s previous image
  always has what it needs, and the deploy keeps it a while longer for the dashboards it can put back, as above.
- `apply_rights` refuses, with the login untouched, while MinerBot's database holds a trigger, an event or a stored
  routine, which no migration makes: "MinerBot's database, <botdb>, holds what no migration makes: <kind name, ...>.
  ... Look at each one, as in SHOW CREATE TRIGGER ...; drop it unless someone here made it on purpose, and find out who
  made it. The dashboard's login wasn't touched." Each would run with its maker's rights on every row the dashboard's
  login adds, the audit log's included, and no rights file takes that away. A pull request whose migration makes one
  changes `bot_db_extras` in `common.sh`.
- The audit log is add-only for the dashboard's login only. The bot's own login has every right in MinerBot's database,
  and so has root. Before `move-database.sh` moved it, the cPanel account that owned the database and the bot's login
  could take them over through cPanel's MySQL tools, then read, change or delete audit rows and sessions.
- Never give this login rights in cPanel: cPanel only knows whole databases.

How the file grows. The approved plan's list is the ceiling:

| Pull request | Adds |
|---|---|
| PR 4, armor review | `SELECT`, `UPDATE (Status)` on `ihvph_armor_styles`; `SELECT, INSERT` on `armor_reviews` |
| PR 5, blacklist and audit page | `SELECT, INSERT, UPDATE, DELETE` on `ihvph_blacklist`; `SELECT` on `audit_log` |
| PR 8, status and servers pages | `SELECT` on `bot_status`, `uptime_samples`, `uptime_daily`, `bot_events`, `job_status`, `job_runs`, `guilds`, `guild_channels`, `guild_events` and `guild_daily`, which only the bot writes; `SELECT` on `ihvph_discord_servers`, each server's ID and four channel IDs |
| The server channels page | `INSERT, UPDATE (armor_channel_id, map_channel_id, lobby_channel_id, announcements_channel_id)` on `ihvph_discord_servers`, whose `SELECT` PR 8 gave |
| PR 9, the bot's requests | `SELECT` and an `INSERT` of the eleven request columns on `bot_commands`, so a request's state, run, times, result and direct message start empty and only the bot's own login writes them; `SELECT` on `bot_settings`, which only the bot writes |
| PR 10, the controls and the Sessions page | `SELECT` on `schema_version`, each migration's number, name, checksum and time, which only the migrations write, for the Sessions page's check of MinerBot's database structure |
| The dashboard's switch and note tables | `SELECT, INSERT, UPDATE, DELETE` on the dashboard's switches and notes, each in the PR that creates the table |

## The website feed's rights

The bot writes the open lobbies and Steam's latest maps for the website's Lobbies and Maps pages into three tables of
the website's database: `mmh_feed_lobbies`, `mmh_feed_maps` and `mmh_feed_state` (the root README, "Website feed"). The
website makes them, in its own migration, and no script here does: no deploy, setup or migration of MinerBot's touches
them, and `dashboard-rights.sql` gives the dashboard's login nothing on them.

The bot's website login, the cPanel login in `bot.env`'s `ConnectionStrings__DefaultConnection`, needs `SELECT`,
`INSERT`, `UPDATE` and `DELETE` on the three, and nothing else for the feed. Give them once, by hand, as root, after
the website has made the tables, since MariaDB refuses a right on a table that doesn't exist yet. `<botlogin>` is that
login's name, on both hosts, as for the other logins:

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON `<sitedb>`.`mmh_feed_lobbies` TO '<botlogin>'@'localhost', '<botlogin>'@'127.0.0.1';
GRANT SELECT, INSERT, UPDATE, DELETE ON `<sitedb>`.`mmh_feed_maps` TO '<botlogin>'@'localhost', '<botlogin>'@'127.0.0.1';
GRANT SELECT, INSERT, UPDATE, DELETE ON `<sitedb>`.`mmh_feed_state` TO '<botlogin>'@'localhost', '<botlogin>'@'127.0.0.1';
```

- A login that cPanel gave every right in `<sitedb>` already has them, and needs none of this.
- The order doesn't matter to the bot. Until the tables are there, and the rights with them, each feed logs one warning,
  with MariaDB's error 1146 for a missing table or 1142 for a missing right, and the bot runs as ever. It tries again
  at least once every 5 minutes, so it starts writing without a restart, and logs one line for each feed when it does:
  `docker logs --since 10m minerbot 2>&1 | grep -F 'website'`.
- To check what the login holds, never `SHOW GRANTS`, which prints the password's hash:
  `SELECT GRANTEE, TABLE_NAME, PRIVILEGE_TYPE FROM information_schema.TABLE_PRIVILEGES WHERE TABLE_SCHEMA = '<sitedb>' AND TABLE_NAME IN ('mmh_feed_lobbies', 'mmh_feed_maps', 'mmh_feed_state');`
- To stop the feed's writes without a deploy, take the rights away again, with `REVOKE` in place of `GRANT` and `FROM`
  in place of `TO`. The pages then show their feeds as behind, by `ok_at`.
- The website owns the tables' structure. A change to it is made in the website's migration first, then copied to
  `docs/schema/wordpress-tables.sql`, which the database tests build their copy from, with the bot's code. A new column
  needs a default until the bot writes it, and a column goes from the bot's code before it goes from the table.

## The settings rule

`rollback.sh` keeps the current `dashboard.env`, so the previous image must accept it. A setting is never renamed or
removed in the pull request that stops using it: the old name stays accepted and written for one more pull request. A
new setting gets an empty-means-off default wherever it can, and setup never writes a value in a form the previous
image's validator refuses. The validators ignore settings they don't know, so a setting a newer pull request added
never stops an older image. The same rule is a comment at the top of `MinerBot.Dashboard/Settings/SettingsValidation.cs`.

The same holds for nginx's file and `compose.yml`'s dashboard section. They must work with the previous dashboard
image: `rollback.sh` runs it behind them, and setup may recreate it under them. A new image must work behind the
previous nginx file too, because a deploy swaps the dashboard before setup installs its file. A change that can't meet
this, such as a new upstream, Host or proxy-key header, is split over pull requests: an image that works both ways,
then the file, then the old way goes.

The same holds for the request the readiness check sends, `home_page_status` in `common.sh`, since a deploy installs
the deploy files forward only. Every dashboard that a deploy, a failed swap's put-back, `rollback.sh` or setup starts
must answer it with 200: `/`, asked through the socket with the public Host, `X-Forwarded-Proto: https`, the proxy key
and no cookie. So the home page keeps answering a visitor who isn't signed in with the page itself, which a comment
in `MinerBot.Dashboard/Pages/Index.cshtml.cs` repeats. A change to that request, such as a renamed proxy-key header,
waits until the previous image answers the new one too.

## Backups and restore

`backup.sh nightly` runs at 03:17 from `/etc/cron.d/minerbot`; every deploy with migrations runs `backup.sh pre-deploy`
first. Each dump is a consistent InnoDB snapshot without locks (`--single-transaction`), with BINARY columns as hex, in
utf8mb4, and without the session rows (`--ignore-table-data`), so a restore signs everyone out. MinerBot's database has
no triggers, events or stored routines; the dump leaves them out (`--skip-triggers --skip-routines --skip-events`, on
the command line, which wins over a `routines` or `events` line in root's option files), and the restore below refuses
a dump that holds one. The rows, each table's `CREATE` and the `DROP` before it are pinned the same way
(`--skip-no-data --skip-no-create-info --add-drop-table`), so a `no-data` line there can't make every dump empty. Options
that take a value, such as `where`, can't be undone on the command line, so root's option files must not set them for
`mysqldump` or `mariadb-dump`. A dump goes to a `.partial` file, is checked with `gzip -t` and for its last line
`-- Dump completed`, and only then gets its final name. Dumps older than 14 days are pruned after a dump that worked,
the newest 3 always kept.

A failure is visible four ways: cron mails root (the cron line's `|| echo` also covers a run stopped at its limit);
`backup.log` has a `failed` line, or, after a restart or a kill, a `started` line with nothing after it; the 05:47
`status.sh --check` reports a newest backup older than 36 hours, a failed last run, or a run that started more than 2
hours ago and never finished; and every deploy's preflight warns. The check runs at 05:47 because a nightly run writes
its `started` line by 03:37 at the latest (03:17, plus up to 15 minutes for the deploy lock and 5 for the backup lock)
and its cron line ends it by 04:18, so a run a restart cut off is over 2 hours old by then and is reported that same
morning. A night whose backup never started, with the server down at 03:17 say, leaves no line at all: the 36-hour
rule reports only a second such night in a row.

Restore, as `README.txt` gives it on the server. Nothing from a dump ever runs as MariaDB's root: `mariadb-dump` writes
a trigger's, event's or routine's text unchecked between `DELIMITER` lines, so text the server stored as one statement
can reach the client as several, and anyone who can make one in MinerBot's database (the bot's login, and, before
`move-database.sh`, the cPanel account that owned it) could have a root restore run SQL of their choosing.

1. `./backup.sh manual`, a dump of the current state first.
2. `docker compose stop dashboard bot`: the bot writes its status and history in this database too. Don't run
   `./setup-dashboard.sh` before step 6: it starts a stopped dashboard.
3. `gzip -t <file>`; `gzip -dc <file> | tail -n 1` must show `-- Dump completed`; and
   `gzip -dc <file> | grep -c '^DELIMITER'` must print 0. A `DELIMITER` line means the dump holds a trigger, event or
   routine, which no migration makes: never restore that dump, as anyone. Look at it with `gzip -dc <file> | less`,
   and find out who made it.
4. To see which migrations the dump has, without running any of it:
   ``gzip -dc <file> | sed -n '/^INSERT INTO `schema_version`/,/;$/p'``, against the live database's:
   `mariadb <BOT_DB from server.conf> -e 'SELECT version, name FROM schema_version'`.
5. Restore it as the bot's own database login, never as root, so the dump can do no more than that login may. The
   login's user and password go from `bot.env` into a file only root can read, the restore runs only when the file has
   both, and the file goes after. `bot.env` is read as Compose and the scripts read it, by `env_value` in `common.sh`:
   the last line wins, and one pair of quotes around the value goes.

   ```bash
   f=$(mktemp /opt/minerbot/state/tmp/restore.XXXXXX)
   { echo '[client]'; bash -c 'source ./common.sh && env_value bot.env ConnectionStrings__BotConnection' | tr ';' '\n' \
       | sed -n -e 's/^ *\(uid\|user id\|user\)=/user=/Ip' \
           -e 's/^ *\(pwd\|password\)=\(.*\)$/password="\2"/Ip'; } > "$f"
   if [ "$(grep -c -e '^user=' -e '^password=' "$f")" = 2 ]; then
       gzip -dc <file> | mariadb --defaults-file="$f" --default-character-set=utf8mb4 <BOT_DB from server.conf>
   else
       echo "bot.env's ConnectionStrings__BotConnection gave no Uid= or Pwd= part, so nothing was restored."
   fi
   rm -f "$f"
   ```

   This replaces each table in the dump, and prints nothing when it works. If MariaDB refuses the login as it connects
   (ERROR 1045, with no "at line"), its password was changed outside `bot.env`: find out why before anything else.
6. If step 4 showed fewer migrations in the dump than in the live database, the restore took them back. Either migrate
   again, from the commit the dashboard runs (`./status.sh` shows it), since every migration can run twice:
   `./deploy.sh <that commit> --only dashboard --no-start`, which builds, dumps and migrates, and swaps nothing; or, to
   undo the last dashboard deploy's migration, `./rollback.sh dashboard`, which goes back to the image from before that
   deploy. Then `docker compose up -d --no-build --no-deps dashboard bot`, then `./status.sh`. The dump's status row
   is the one from when it was made, so the bot's first write may record `bot.unclean_end` for that run, which was no
   crash.

## Tests

CI's required "Build and test" job runs, after the .NET tests:

| Step | What it catches |
|---|---|
| `shellcheck -x -P SCRIPTDIR deploy/*.sh deploy/tests/*.sh deploy/tests/fakes/*` | Quoting and syntax in scripts the server can't check |
| `docker compose -f deploy/compose.yml config --quiet` (with an empty `deploy/bot.env`, which `*.env` keeps out of Git) | YAML and schema mistakes |
| `bash deploy/tests/nginx-test.sh` | 26 checks of the nginx file in `nginx:1.31.8-alpine`, next to a cPanel-like configuration, with a stand-in dashboard on the socket: headers, the Cloudflare-only rule, body caps, timeouts, logs, the 503 page, `pipelog.conf`, and that nothing reaches 127.0.0.1:5080 |
| `bash deploy/tests/nginx-procedure-test.sh` | 23 checks of the real `nginx_change`, `nginx_remove`, `nginx_undo_cut_off`, `nginx_look`, the reload's proof and cPanel's pipelog hook against a real nginx 1.31.8, cut-off runs, an undo after WHM's piped logging changed, and reloads nginx doesn't take included |
| `bash deploy/tests/rights-test.sh` | 22 checks of the real `apply_rights` against MariaDB 11.4: the rights, the grant options, the copy setup applies, the refusal of triggers, events and routines, `move-database.sh`'s steps (the bot's login, the copy and its comparison, and the rights on the new database), and the request table's columns the dashboard can't write |
| `bash deploy/tests/scripts-test.sh` | 79 cases of the deploy, rollback, setup, bootstrap, move-database, fill-secrets, backup, status and request logic in `almalinux:9`, the server's bash and coreutils, with stand-ins for Docker, MariaDB, nginx, systemd and the rest |
| `bash deploy/tests/image-files-test.sh` | 5 checks of both images, built from files only root can read, as `deploy.sh`'s checkout under umask 077 left them: their user, 1654, can read every file in `/app`, and owns and can change nothing there but its volume's folder |

The job has 125 minutes, and each of the five test steps a limit above its script's own: 15 minutes for
`nginx-test.sh`, 13 for `nginx-procedure-test.sh`, 10 for `rights-test.sh`, 30 for `scripts-test.sh` and 35 for
`image-files-test.sh`, whose two builds get 15 minutes each.

`scripts-test.sh`'s cases, by number:

| Cases | What they cover |
|---|---|
| 1-12, 24-39, 60-65 | `deploy.sh` and `rollback.sh`: refusals, the first build, deploy files, full deploys, swaps that fail and go back, time limits, refs and main's first-parent line, Compose failing, a `compose.yml` a service can't run under, the socket, rewritten history, locks, units, readiness, an older dashboard's rights, cut-off swaps, following a run while `systemctl` doesn't answer, a swap Compose can't put back, the rights every dashboard that can be put back keeps, and a dashboard that can't render a page |
| 66-69 | `move-database.sh`: a move and `--drop-old`, the refusals before anything changes, a copy that differs and a dashboard that isn't ready on the new database, each going back, and a run cut off after each step |
| 13-16, 21, 50-59, 75 | `setup-dashboard.sh`: the first setup through a real terminal, nginx failures, converging, refusals, no terminal, the rights copy, password changes that stop, hidden pastes, a stopped dashboard, nginx's file waiting for its dashboard, a recreate Compose didn't do, a stale `pipelog.conf` or hook, a lone reload nginx didn't take, a stop after the undo of a cut-off nginx change, which names that undo |
| 76-79 | `setup-dashboard.sh` and the request-signing key: one made when there's none, `bot.env` first, the dashboard and then the bot recreated; `--rotate-request-key`; a run cut off after `bot.env`; a bot on another key, one that's stopped, and one that isn't ready after its recreate; malformed keys; no `bot.env` |
| 70-73 | `request.sh`: settings and the text by name only, the history line and each exit code; each refusal, and an image from before the request command; the wait after a restart while it holds the lock; the lock another run holds |
| 90-92 | a reload nginx doesn't take, `pipelog.conf` following WHM's piped logging, and triggers, events or routines in MinerBot's database |
| 17, 42 | `backup.sh` |
| 18, 43, 45-47 | `status.sh`, the public path, `pipelog.conf`, cPanel's hook, MinerBot's database and the request-signing key included |
| 22, 44 | `fill-secrets.sh` |
| 23, 40, 41 | `bootstrap.sh` |
| 19, 20 | across every case: no secret in any output, and the static rules; these two run last |

Each test script starts only `--rm` containers with time limits, and removes its networks and anonymous volumes in a
trap. Their containers end by themselves too: `nginx-test.sh`'s after 10 minutes, `nginx-procedure-test.sh`'s after 7,
`rights-test.sh`'s after 5, and `scripts-test.sh`'s after 25, whose cases get 20 at most. Docker picks
`nginx-test.sh`'s subnet for each run, so runs side by side don't clash; a killed run leaves its network, which
`docker network prune --filter label=mbtest-nginx` removes. `image-files-test.sh` removes its two images when it ends;
their build cache stays, so the next run is quicker, and `docker builder prune` removes it. A run of it killed during a
build can leave its scratch folder, `mbtest-files.*` in TMPDIR, and, once that build ends, its images, which
`docker image prune -a -f --filter label=mbtest-files` removes. `MBTEST_CASES="5 17"` runs only those cases of
`scripts-test.sh`, and `MBTEST_SAVE=<folder>` keeps its outputs. Each stand-in in `tests/fakes/` says at its top what
it records and which `FAKE_*` variables change what it does.
`deploy/tests/fixtures/compose-before-pr3.yml` is the server's compose file from before these deploy files, word for
word. `deploy/tests/local-stack.sh up|check|down` is a manual test of the whole stack (MariaDB, the dashboard image and
nginx sharing one loopback, as host networking does, and the dashboard's socket in a volume they share), including the
bootstrap's bot check with real Compose; it isn't in CI.

On this Windows machine, in Git Bash, with temporary files off the nearly full C: drive:

```bash
export TMPDIR=G:/Temp MSYS_NO_PATHCONV=1   # any folder on a drive with room, not C:
bash deploy/tests/nginx-test.sh
bash deploy/tests/nginx-procedure-test.sh
bash deploy/tests/rights-test.sh
bash deploy/tests/scripts-test.sh
bash deploy/tests/image-files-test.sh
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

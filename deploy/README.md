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
| `minerbot-dashboard.tmpfiles` | `/etc/tmpfiles.d/minerbot-dashboard.conf`, from which systemd-tmpfiles makes the dashboard's socket folder at every boot | 644 |
| `bootstrap.sh`, `server.conf.example`, `README.md`, `tests/` | not installed | |

The server's layout, all of it root's but the socket's folder:

```
/opt/minerbot/                700
  server.conf                 600  SITE_DB, BOT_DB, BOT_READY_PATTERN (bootstrap.sh writes it; never in Git)
  bot.env  dashboard.env  dashboard.env.previous    600
  compose.yml common.sh README.txt dashboard-rights.sql rights-list.sql minerbot-dashboard.conf   600
  deploy.sh rollback.sh setup-dashboard.sh fill-secrets.sh backup.sh status.sh                     700
  src/                        the clone deploy.sh builds from, detached at the deployed commit
  state/                      700: lock, lock.info, backup.lock, deploy-files.commit, bootstrap.done,
                                   dashboard-rights.applied.sql (600, the rights file the last apply used),
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
   ever runs from `src/`, because bash reads a script as it runs it.
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
| `./setup-dashboard.sh` | Asks for what only the owner has (the Origin certificate and key, neither shown as it's pasted, the Discord client ID and secret, the owners and admins, Cloudflare Access's team and AUD), then changes only what differs. Enter keeps a value, `keep` keeps a paste. | `minerbot-setup` | 30 min |
| `./setup-dashboard.sh --no-questions [--yes]` | Keeps every value. Installs a changed nginx file once the dashboard of its commit runs, applies again the rights the last apply used, starts the dashboard when its container is stopped, recreates it when it runs on older settings than `dashboard.env` holds (after a recreate that failed), and repairs whatever differs. It asks "Go ahead?" unless `--yes`; without a terminal and without `--yes` it changes nothing. | `minerbot-setup` | 30 min |
| `--rotate-proxy-key`, `--rotate-db-password` | Makes a new proxy key or database password; goes with either form above. | | |
| `./setup-dashboard.sh --remove-nginx [--yes]` | The emergency path when `nginx -t` fails and names MinerBot's file: undoes a cut-off change first, takes the file out of nginx, keeping a copy, tests and reloads. Works while `nginx -t` fails. Nothing else: `/etc/nginx/minerbot`, with `pipelog.conf`, and cPanel's hook stay. | `minerbot-setup` | 30 min |
| `./fill-secrets.sh` | Asks for `BOTTOKEN`, `WEBHOOKCHANNEL`, `STEAMWEB_PUB_KEY` and `STEAMUSER_NAME`, hidden, Enter keeps; writes them into `bot.env` and prints the recreate command. Holds the lock while it runs. | terminal | 10 min a question |
| `./backup.sh nightly\|pre-deploy\|manual` | Dumps MinerBot's database into `/srv/backups/minerbot` and keeps 14 days, the newest 3 always. Prints nothing when it works and one line when it fails. | cron, deploy | its own limits, 2,400 s at most |
| `./status.sh` | A read-only look: containers, images, history, a running action, backups, the certificate, `nginx -t`, cPanel's Cloudflare list, the dashboard's socket, its proxy key while MinerBot's file is in nginx, the public home page's status, the disk. | terminal | 30 s a probe |
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

Ready means, for the dashboard, within 90 seconds: its container runs the image it must, with no new restart, and
isn't unhealthy; its socket is as nginx needs it; and `/health/ready`, asked through the socket, answers `Healthy`,
which proves both databases with its own login and rights. When the public home page answered 200 before the swap, it
must answer 200 again within 30 seconds. For the bot, until PR 6 gives it a status the deploy can ask: a line matching
`BOT_READY_PATTERN` from `server.conf` in its log within 150 seconds, and 20 seconds later it still runs with the same
start time and no new restart. A container on another image than the one it must run fails at once.

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

### The nightly check

`./status.sh --check` runs at 05:47 from `/etc/cron.d/minerbot`. It reports, one line each:

- no dump newer than 36 hours, a last backup that failed, or one that started more than 2 hours ago and never wrote
  another line;
- the Origin certificate ending within 30 days;
- `nginx -t` failing twice, a minute apart, so a reload in progress isn't reported;
- the bot not running, its container missing, or Docker not answering;
- with `dashboard.env` there: the dashboard not running or unhealthy; its socket not as nginx needs it (a missing
  socket only while the dashboard runs); and the public home page answering anything but 200 twice, a minute apart,
  while the dashboard runs. A Cloudflare challenge (`cf-mitigated: challenge`, as Bot Fight Mode may send the server)
  isn't reported, since it says nothing about the dashboard;
- with `dashboard.env` there and MinerBot's file in nginx too: `/etc/nginx/minerbot/proxy-key.conf` missing, or not
  holding the proxy key the dashboard runs with (its container's, or `dashboard.env`'s when there's no container); and
  nginx's workers older than that file, so nginx hasn't loaded it (only when the file is right and no nginx change is
  cut off). After `./setup-dashboard.sh --remove-nginx`, neither is looked at until setup puts the file back;
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
   and makes its tables.
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
  `curl -s --max-time 5 --unix-socket /run/minerbot-dashboard/dashboard.sock -H 'Host: localhost' http://localhost/health/ready`.
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
  and the cPanel account it's mapped to can take it over through cPanel's MySQL tools, then read, change or delete
  audit rows and sessions.
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

The same holds for nginx's file and `compose.yml`'s dashboard section. They must work with the previous dashboard
image: `rollback.sh` runs it behind them, and setup may recreate it under them. A new image must work behind the
previous nginx file too, because a deploy swaps the dashboard before setup installs its file. A change that can't meet
this, such as a new upstream, Host or proxy-key header, is split over pull requests: an image that works both ways,
then the file, then the old way goes.

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
can reach the client as several, and anyone who can make one in MinerBot's database (the bot's login, and the cPanel
account it's mapped to) could have a root restore run SQL of their choosing. PR 6, which gives the bot this database,
updates steps 2 and 6.

1. `./backup.sh manual`, a dump of the current state first.
2. `docker compose stop dashboard`, and the bot too once it uses this database. Don't run `./setup-dashboard.sh` before
   step 6: it starts a stopped dashboard.
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
   deploy. Then `docker compose up -d --no-build --no-deps dashboard`, and the bot too if step 2 stopped it, then
   `./status.sh`.

## Tests

CI's required "Build and test" job runs, after the .NET tests:

| Step | What it catches |
|---|---|
| `shellcheck -x -P SCRIPTDIR deploy/*.sh deploy/tests/*.sh deploy/tests/fakes/*` | Quoting and syntax in scripts the server can't check |
| `docker compose -f deploy/compose.yml config --quiet` (with an empty `deploy/bot.env`, which `*.env` keeps out of Git) | YAML and schema mistakes |
| `bash deploy/tests/nginx-test.sh` | 26 checks of the nginx file in `nginx:1.31.8-alpine`, next to a cPanel-like configuration, with a stand-in dashboard on the socket: headers, the Cloudflare-only rule, body caps, timeouts, logs, the 503 page, `pipelog.conf`, and that nothing reaches 127.0.0.1:5080 |
| `bash deploy/tests/nginx-procedure-test.sh` | 23 checks of the real `nginx_change`, `nginx_remove`, `nginx_undo_cut_off`, `nginx_look`, the reload's proof and cPanel's pipelog hook against a real nginx 1.31.8, cut-off runs, an undo after WHM's piped logging changed, and reloads nginx doesn't take included |
| `bash deploy/tests/rights-test.sh` | 19 checks of the real `apply_rights` against MariaDB 11.4: the rights, the grant options, the copy setup applies, and the refusal of triggers, events and routines |
| `bash deploy/tests/scripts-test.sh` | 65 cases of the deploy, rollback, setup, bootstrap, fill-secrets, backup and status logic in `almalinux:9`, the server's bash and coreutils, with stand-ins for Docker, MariaDB, nginx, systemd and the rest |
| `bash deploy/tests/image-files-test.sh` | 5 checks of both images, built from files only root can read, as `deploy.sh`'s checkout under umask 077 left them: their user, 1654, can read every file in `/app`, and owns and can change nothing there but its volume's folder |

The job has 125 minutes, and each of the five test steps a limit above its script's own: 15 minutes for
`nginx-test.sh`, 13 for `nginx-procedure-test.sh`, 10 for `rights-test.sh`, 30 for `scripts-test.sh` and 35 for
`image-files-test.sh`, whose two builds get 15 minutes each.

`scripts-test.sh`'s cases, by number:

| Cases | What they cover |
|---|---|
| 1-12, 24-39, 60-64 | `deploy.sh` and `rollback.sh`: refusals, the first build, deploy files, full deploys, swaps that fail and go back, time limits, refs and main's first-parent line, Compose failing, a `compose.yml` a service can't run under, the socket, rewritten history, locks, units, readiness, an older dashboard's rights, cut-off swaps, following a run while `systemctl` doesn't answer, a swap Compose can't put back, and the rights every dashboard that can be put back keeps |
| 13-16, 21, 50-59, 75 | `setup-dashboard.sh`: the first setup through a real terminal, nginx failures, converging, refusals, no terminal, the rights copy, password changes that stop, hidden pastes, a stopped dashboard, nginx's file waiting for its dashboard, a recreate Compose didn't do, a stale `pipelog.conf` or hook, a lone reload nginx didn't take, a stop after the undo of a cut-off nginx change, which names that undo |
| 90-92 | a reload nginx doesn't take, `pipelog.conf` following WHM's piped logging, and triggers, events or routines in MinerBot's database |
| 17, 42 | `backup.sh` |
| 18, 43, 45, 46 | `status.sh`, the public path, `pipelog.conf`, cPanel's hook and MinerBot's database included |
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

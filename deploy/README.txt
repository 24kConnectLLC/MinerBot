MinerBot on this server
=======================

Everything for MinerBot is in /opt/minerbot, which only root can open. The scripts, compose.yml and this file come from
the deploy/ folder of https://github.com/24kConnectLLC/MinerBot: deploy.sh installs them from the commit it deploys,
and only ever moves them forward. Change them there, never here. deploy/README.md in the repository has the details,
the first setup's steps in Cloudflare and Discord, and how to take the dashboard away for good.

The Origin certificate ends {{ORIGIN_CERT_ENDS}}: make a new one in Cloudflare before then and run ./setup-dashboard.sh.


Where everything is
-------------------

  server.conf              this server's database names and the bot's ready line (bootstrap.sh wrote it; never in Git);
                           the connection strings in bot.env and dashboard.env name the databases too
  bot.env                  the bot's settings and secrets (./fill-secrets.sh changes the secrets)
  dashboard.env            the dashboard's settings and secrets (./setup-dashboard.sh writes it)
  dashboard.env.previous   dashboard.env as it was before setup's last change
  compose.yml              how both containers run
  deploy.sh, rollback.sh, setup-dashboard.sh, fill-secrets.sh, backup.sh, status.sh, and common.sh, which they share
  dashboard-rights.sql     everything the dashboard's database login may do; rights-list.sql lists a login's rights
  minerbot-dashboard.conf  the reference copy of nginx's file, which only setup-dashboard.sh puts into nginx
  logs/                    one log per run (<UTC>-deploy.log and so on, the newest 60), history.log, and
                           failed-<service>-<UTC>.log: the last log lines of a container a deploy took back out
  state/                   the lock and lock.info, deploy-files.commit, nginx-backup/ (the newest 3), bootstrap.done,
                           dashboard-rights.applied.sql (the rights the last apply used, which setup applies again),
                           and compose-ready-bot.yml and compose-ready-dashboard.yml (the compose.yml each service was
                           last ready under, which a failed swap puts back)
  src/                     the clone deploy.sh builds from, at the deployed commit
  old/                     the files from before the move to these deploy files, kept as they were

  /etc/cron.d/minerbot                         the nightly backup (03:17) and the nightly check (05:47)
  /etc/tmpfiles.d/minerbot-dashboard.conf      makes the dashboard's socket folder, /run/minerbot-dashboard, at boot
  /run/minerbot-dashboard/dashboard.sock       the dashboard's socket, where nginx reaches it: it listens on no port
  /etc/nginx/conf.d/minerbot-dashboard.conf    nginx's file for the dashboard, with no secret in it
  /etc/nginx/minerbot/                         the certificate, its key, the proxy key and pipelog.conf, which only
                                               root can open
  /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog
                                               keeps pipelog.conf in step with WHM's piped logging; cPanel runs it
  /srv/backups/minerbot/                       the dumps of MinerBot's database, and backup.log
  /var/log/nginx/minerbot-dashboard.access.log who asked for which path, without query strings
  /var/log/nginx/minerbot-dashboard.error.log  when nginx couldn't reach the dashboard: the visitor's address, the
                                               request line with its query string, and the referrer
  cPanel's logrotate rotates both nginx logs every day and keeps 21, but skips a day without lines, so a quiet log
  keeps its lines longer than 21 days, the error log for months.


Everyday commands, in /opt/minerbot
-----------------------------------

  ./status.sh                                   what runs, the last deploys, backups, the certificate, nginx, the
                                                dashboard's socket and proxy key, the public home page, the disk
  docker logs -f --tail 50 minerbot-bot         the bot's log; minerbot-dashboard for the dashboard's
  docker compose restart bot                    a restart with the same settings
  docker compose stop bot                       stop it; start it again with: docker compose up -d --no-build --no-deps bot
  ./deploy.sh [ref] [--only bot|dashboard] [--no-start]
                                                build from GitHub and swap in, the dashboard first (ref: main)
  ./rollback.sh bot|dashboard                   back to a service's previous image; run it again to go forward again
  tail logs/history.log                         what was deployed, rolled back or set up, and when
  tail -n 50 -f "$(ls -t logs/*-deploy.log | head -n 1)"
                                                follow a deploy again after its window closed (also -rollback, -setup)
  git -C src log --first-parent --oneline refs/remotes/origin/main
                                                the commits deploy.sh deploys, as of its last fetch: only those main
                                                itself pointed at, never one inside a merged pull request

Deploys, rollbacks, setup's changes and the bootstrap run as systemd units of their own (minerbot-deploy, and so on)
with hard time limits. Closing the window or losing SSH doesn't stop them, and a second one can't start while one runs.
systemctl status minerbot-deploy shows a deploy that runs.


Settings are read only when a container is created
--------------------------------------------------

After editing a setting by hand: docker compose up -d --no-build --no-deps --force-recreate <service>
docker restart and docker compose restart keep the old values. Always name the service. setup-dashboard.sh recreates
the dashboard itself when its settings change. The dashboard's socket, Dashboard__UnixSocket, comes from compose.yml,
which wins over dashboard.env.

Values in bot.env and dashboard.env follow one safe-character rule: printable ASCII only; none of $, ", ', \ and the
backtick; no space at the start or the end; no # right after a space; spaces inside a value are fine. Compose and
docker run --env-file take any other value differently.


When a setting changes
----------------------

  Owners, admins, the Discord client ID or secret, Access on or off, a new AUD, a new certificate
      ./setup-dashboard.sh: give the new value, Enter (or keep, for a paste) for the rest. It recreates the dashboard or
      reloads nginx only as needed.
  An admin or owner joins or leaves while Access is on
      Both: their email in Cloudflare's Access policy (Zero Trust > Access > Applications > MinerBot admin), and their
      Discord ID with ./setup-dashboard.sh.
  The proxy key or the dashboard's database password is suspected leaked
      ./setup-dashboard.sh --no-questions --rotate-proxy-key, or --rotate-db-password
  A pull request changed the nginx file (deploy.sh says so, once the dashboard of that commit runs)
      ./setup-dashboard.sh --no-questions (over SSH without a terminal, with the owner's go-ahead: --no-questions --yes)
  WHM's piped logging goes off or on
      Nothing: cPanel runs MinerBot's hook, which keeps pipelog.conf in step.
  The bot's token, webhook, Steam key or Steam user
      ./fill-secrets.sh, then: docker compose up -d --no-build --no-deps --force-recreate bot
  Anything else in bot.env
      Edit it, with values by the safe-character rule above, then the same recreate.
  The database rights
      A pull request changes deploy/dashboard-rights.sql, and the next dashboard deploy applies it. Setup applies again
      only the rights the last apply used, never a newer file.
  The database names
      /opt/minerbot/server.conf, and the same names in bot.env's two connection strings. Then, before any dashboard
      deploy, which would move the rights first: ./setup-dashboard.sh --no-questions, which rewrites dashboard.env's
      connection strings, moves the dashboard's rights, and may give its login a new password. Then:
      docker compose up -d --no-build --no-deps --force-recreate bot
      A database renamed outside cPanel also needs the bot's login's grants moved.


When something goes wrong
-------------------------

A deploy failed.
  It says which step. A stop before the swap left everything running as it was. A swap counts as failed when Compose
  fails, when the container doesn't then run the new image, or when it isn't ready: that service is back on its old
  image, the new one is tagged :failed, and its last log lines are in logs/failed-<service>-<UTC>.log. If compose.yml
  had changed that service's settings, the compose.yml it was last ready under is back in place too, until the next
  deploy installs the new one again. Fix the cause and deploy again.

A deploy refuses the commit.
  "... isn't one of main's own commits": deploy.sh deploys only commits main itself pointed at, never a branch or a
  commit inside a merged pull request; git -C src log --first-parent --oneline refs/remotes/origin/main lists them.
  "Couldn't fetch from GitHub within 2 minutes": git's own reason is above that line.

A deploy or rollback was cut off.
  Its log ends without a RESULT line. When ./status.sh then shows a service's container on another image than its
  current tag, running or stopped, it was cut off after it moved the tags: run ./rollback.sh <service>, which first
  finishes that swap on the current tag, and moves no tag. Run it once more to go back to the previous image.
  Otherwise nothing was left half done: deploy again.

nginx -t fails.
  If it names MinerBot's file: ./setup-dashboard.sh --remove-nginx, which works even then; then fix the cause and run
  ./setup-dashboard.sh. If --remove-nginx says the problem is in another file, it has put ours back and not reloaded.
  If it says unknown directive "pipelog" in /etc/nginx/minerbot/pipelog.conf, that file is stale: run
  /etc/nginx/ea-nginx/config-scripts/global/minerbot-pipelog, then /scripts/restartsrv_nginx --reload.
  When the nightly check says pipelog.conf doesn't match cPanel's pipelog module, or that the hook is missing, while
  nginx -t passes: ./setup-dashboard.sh --no-questions writes both again.
  If the problem is cPanel's: /usr/local/cpanel/scripts/ea-nginx config --all, or the host's support.

nginx doesn't take a reload.
  Setup says "cPanel's reload exited 0, but nginx kept its old worker processes": nginx can't apply its files, as when
  a listen address is held by another program, which nginx -t never tries. It then takes no reload, AutoSSL's included.
  grep -F '[emerg]' /var/log/nginx/error.log | tail -n 5 says why. Fix that, run /scripts/restartsrv_nginx --reload,
  then ./setup-dashboard.sh --no-questions.

Looking at nginx's whole configuration.
  Never share raw nginx -T output: it prints root-only files, MinerBot's proxy key and cPanel's own proxy secret among
  them. Use ./status.sh --nginx-look.

A setup run was cut off.
  ./status.sh shows the nginx change marker when it was cut off during nginx's change. Run ./setup-dashboard.sh
  --no-questions: it undoes that change first, from its backup, and starts the dashboard if the cut-off run had stopped
  it. A --rotate-proxy-key cut off after nginx's change keeps nginx's new key, which dashboard.env then takes. One cut
  off before it leaves the old key: run ./setup-dashboard.sh --no-questions --rotate-proxy-key again.

The dashboard never gets ready, or it's stopped.
  ./status.sh says why, the socket included. ./setup-dashboard.sh --no-questions starts a dashboard whose container is
  stopped, as does docker compose up -d --no-build --no-deps dashboard, and recreates one that runs on older settings
  than dashboard.env holds, as after a recreate that failed. Its socket's folder and socket must show
  2750 1654 nobody directory and 660 1654 nobody socket:
    stat -c '%a %u %G %F' /run/minerbot-dashboard /run/minerbot-dashboard/dashboard.sock
  systemd-tmpfiles --create /etc/tmpfiles.d/minerbot-dashboard.conf puts the folder right. Check the keys volume with
    docker run --rm --network none -v minerbot-dashboard-keys:/k mcr.microsoft.com/dotnet/runtime:10.0 ls -ln /k

Every visitor gets the dashboard's 404.
  nginx sends another proxy key than the dashboard has. The nightly check says proxy-key.conf is missing or doesn't
  hold the dashboard's key, or that the public home page answered 404 twice: run ./setup-dashboard.sh --no-questions
  --rotate-proxy-key, which gives nginx and the dashboard a new key together, with a few seconds of the restarting
  page. If it says nginx's workers started before proxy-key.conf last changed, run /scripts/restartsrv_nginx --reload
  instead. Another status (403, 52x) or no answer means nginx or Cloudflare doesn't let visitors through: look at
  ./status.sh and /var/log/nginx/minerbot-dashboard.error.log.

A deploy or setup refuses: MinerBot's database "holds what no migration makes".
  A trigger, event or stored routine is in it, which the nightly check also names. Look at each one, as in
  SHOW CREATE TRIGGER `<BOT_DB from server.conf>`.`<name>`; drop it unless someone here made it on purpose, and find
  out who made it. The dashboard's login wasn't touched. Then run the deploy or setup again.

The bot is never ready.
  Check its lines in the run's log, Discord's status page (https://discordstatus.com), or its token (./fill-secrets.sh).

The certificate ends soon.
  Make a new Origin certificate in Cloudflare (SSL/TLS > Origin Server: RSA 2048, only bot.murderminershub.com, 15
  years) and run ./setup-dashboard.sh. After it ends, Cloudflare shows error 526.

Access refuses people.
  docker logs minerbot-dashboard 2>&1 | grep -i 'Access check' gives the reason. "missing" means the Access path doesn't
  cover /admin; "wrong audience" means the AUD changed (a recreated application gets a new one); "wrong issuer" means
  the team domain is wrong. Someone stopped at Cloudflare's PIN page isn't in the Access policy's emails yet.

Adding or removing an admin while Access is on.
  Two changes, both needed: their email in Cloudflare's Access policy (Zero Trust > Access > Applications > MinerBot
  admin), and their Discord ID with ./setup-dashboard.sh.

The disk fills.
  df -h / /srv /var, docker system df, then docker builder prune -f --filter until=72h

A backup failed.
  ./backup.sh manual shows the step. /srv/backups/minerbot/backup.log has a line for each run.

Taking the dashboard off nginx safely.
  ./setup-dashboard.sh --remove-nginx first; only then rm -r /etc/nginx/minerbot. Never remove that folder while
  /etc/nginx/conf.d/minerbot-dashboard.conf exists: nginx would refuse every later reload, AutoSSL's included, and a
  restart would leave it down for every site. To take the dashboard away for good, follow "Taking the dashboard away"
  in deploy/README.md, in its order: the scripts and this file go last.


Backups
-------

Every night at 03:17, ./backup.sh nightly dumps MinerBot's own database into /srv/backups/minerbot and keeps 14 days of
dumps, the newest 3 always. A deploy with migrations dumps first too. Session rows are left out. backup.log has a line
for each run. A failure is mailed to root, and the 05:47 check reports a backup that is old, failed or never finished.
./status.sh --mail-test proves the mail arrives. The website's database isn't in these dumps.

To restore one. Nothing from a dump ever runs as MariaDB's root: a trigger, event or routine in a dump can hide SQL
that would run with the rights of whoever restores it, and the bot's login, or the cPanel account it belongs to, can
make one.
  1. ./backup.sh manual, a dump of the current state first.
  2. docker compose stop dashboard, and the bot too once it uses this database. Don't run ./setup-dashboard.sh before
     step 6: it starts a stopped dashboard.
  3. gzip -t <file>, and gzip -dc <file> | tail -n 1 must show: -- Dump completed
     gzip -dc <file> | grep -c '^DELIMITER' must print 0. Otherwise the dump holds a trigger, event or routine, which
     no migration makes: never restore it, as anyone. Look at it with gzip -dc <file> | less, and find out who made it.
  4. The migrations the dump has, without running any of it, and the live database's:
       gzip -dc <file> | sed -n '/^INSERT INTO `schema_version`/,/;$/p'
       mariadb <BOT_DB from server.conf> -e 'SELECT version, name FROM schema_version'
  5. Restore it as the bot's own database login, never as root. Its user and password go from bot.env into a file only
     root can read, the restore runs only when that file has both, and the file goes after:
       f=$(mktemp /opt/minerbot/state/tmp/restore.XXXXXX)
       { echo '[client]'; sed -n 's/^ConnectionStrings__BotConnection=//p' bot.env | tail -n 1 | tr ';' '\n' \
           | sed -n -e 's/^ *\(uid\|user id\|user\)=/user=/Ip' \
               -e 's/^ *\(pwd\|password\)=\(.*\)$/password="\2"/Ip'; } > "$f"
       if [ "$(grep -c -e '^user=' -e '^password=' "$f")" = 2 ]; then
           gzip -dc <file> | mariadb --defaults-file="$f" --default-character-set=utf8mb4 <BOT_DB from server.conf>
       else
           echo "bot.env's ConnectionStrings__BotConnection has no Uid= or Pwd= part, so nothing was restored."
       fi
       rm -f "$f"
     This replaces each table in the dump. Sessions come back empty, so everyone signs in again, which is intended.
     If MariaDB refuses the login as it connects (ERROR 1045, with no "at line"), its password was changed outside
     bot.env: find out why first.
  6. If step 4 showed fewer migrations in the dump than in the live database, the restore took them back. Either
     migrate again, from the commit the dashboard runs (./status.sh shows it), since every migration can run twice:
       ./deploy.sh <that commit> --only dashboard --no-start      it builds, dumps and migrates, and swaps nothing
     or, to undo the last dashboard deploy's migration, go back to the image from before it: ./rollback.sh dashboard
     Then: docker compose up -d --no-build --no-deps dashboard, and the bot too if step 2 stopped it; then ./status.sh


Housekeeping
------------

  docker system df                    the space images and the build cache use
  docker images minerbot-bot          tags named after a commit, from before these deploy files, can go:
                                      docker rmi minerbot-bot:<commit>


The bot's Steam sign-in
-----------------------

If Steam rejects the bot's saved token, the bot sends a direct message and lobbies stay offline until someone signs in
again. Scan the newest QR code in the bot's log with the Steam Mobile App:
  docker compose restart bot
  docker logs -f --tail 40 minerbot-bot
A new code appears about every 20 seconds for 10 minutes.

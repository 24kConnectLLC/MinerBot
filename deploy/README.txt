MinerBot on this server
=======================

Everything for MinerBot is in /opt/minerbot, which only root can open. The scripts, compose.yml and this file come from
the deploy/ folder of https://github.com/24kConnectLLC/MinerBot: deploy.sh installs them from the commit it deploys,
and only ever moves them forward. Change them there, never here. deploy/README.md in the repository has the details.

The Origin certificate ends {{ORIGIN_CERT_ENDS}}: make a new one in Cloudflare before then and run ./setup-dashboard.sh.


Where everything is
-------------------

  server.conf              this server's database names and the bot's ready line (bootstrap.sh wrote it; never in Git)
  bot.env                  the bot's settings and secrets (./fill-secrets.sh changes the secrets)
  dashboard.env            the dashboard's settings and secrets (./setup-dashboard.sh writes it)
  dashboard.env.previous   dashboard.env as it was before setup's last change
  compose.yml              how both containers run
  deploy.sh, rollback.sh, setup-dashboard.sh, fill-secrets.sh, backup.sh, status.sh, and common.sh, which they share
  dashboard-rights.sql     everything the dashboard's database login may do; rights-list.sql lists a login's rights
  minerbot-dashboard.conf  the reference copy of nginx's file, which only setup-dashboard.sh puts into nginx
  logs/                    one log per run (<UTC>-deploy.log and so on, the newest 60), history.log, and
                           failed-<service>-<UTC>.log: the last log lines of a container a deploy took back out
  state/                   the lock and lock.info, deploy-files.commit, nginx-backup/ (the newest 3), bootstrap.done
  src/                     the clone deploy.sh builds from, at the deployed commit
  old/                     the files from before the move to these deploy files, kept as they were

  /etc/cron.d/minerbot                         the nightly backup (03:17) and the nightly check (03:47)
  /etc/nginx/conf.d/minerbot-dashboard.conf    nginx's file for the dashboard, with no secret in it
  /etc/nginx/minerbot/                         the certificate, its key and the proxy key, which only root can open
  /srv/backups/minerbot/                       the dumps of MinerBot's database, and backup.log
  /var/log/nginx/minerbot-dashboard.access.log who asked for which path, without query strings; 21 days kept


Everyday commands, in /opt/minerbot
-----------------------------------

  ./status.sh                                   what runs, the last deploys, backups, the certificate, nginx, the disk
  docker logs -f --tail 50 minerbot-bot         the bot's log; minerbot-dashboard for the dashboard's
  docker compose restart bot                    a restart with the same settings
  docker compose stop bot                       stop it; start it again with: docker compose up -d --no-build --no-deps bot
  ./deploy.sh [ref] [--only bot|dashboard] [--no-start]
                                                build from GitHub and swap in, the dashboard first (ref: main)
  ./rollback.sh bot|dashboard                   back to a service's previous image; run it again to go forward again
  tail logs/history.log                         what was deployed, rolled back or set up, and when
  tail -n 50 -f "$(ls -t logs/*-deploy.log | head -n 1)"
                                                follow a deploy again after its window closed (also -rollback, -setup)

Deploys, rollbacks, setup's changes and the bootstrap run as systemd units of their own (minerbot-deploy, and so on)
with hard time limits. Closing the window or losing SSH doesn't stop them, and a second one can't start while one runs.
systemctl status minerbot-deploy shows a deploy that runs.


Settings are read only when a container is created
--------------------------------------------------

After editing a setting by hand: docker compose up -d --no-build --no-deps --force-recreate <service>
docker restart and docker compose restart keep the old values. Always name the service. setup-dashboard.sh recreates
the dashboard itself when its settings change.

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
  A pull request changed the nginx file (deploy.sh says so)
      ./setup-dashboard.sh --no-questions (over SSH without a terminal, with the owner's go-ahead: --no-questions --yes)
  The bot's token, webhook, Steam key or Steam user
      ./fill-secrets.sh, then: docker compose up -d --no-build --no-deps --force-recreate bot
  Anything else in bot.env
      Edit it, with values by the safe-character rule above, then the same recreate.
  The database rights
      A pull request changes deploy/dashboard-rights.sql, and the next dashboard deploy applies it.
  The database names
      /opt/minerbot/server.conf


When something goes wrong
-------------------------

A deploy failed.
  It says which step. A stop before the swap left everything running as it was. A service that wasn't ready is back on
  its old image, and its last log lines are in logs/failed-<service>-<UTC>.log. Fix the cause and deploy again.

nginx -t fails.
  If it names MinerBot's file: ./setup-dashboard.sh --remove-nginx, which works even then; then fix the cause and run
  ./setup-dashboard.sh. If --remove-nginx says the problem is in another file, it has put ours back and not reloaded.
  If the problem is cPanel's: /usr/local/cpanel/scripts/ea-nginx config --all, or the host's support.

Looking at nginx's whole configuration.
  Never share raw nginx -T output: it prints root-only files, MinerBot's proxy key and cPanel's own proxy secret among
  them. Use ./status.sh --nginx-look.

An nginx change was cut off.
  ./status.sh shows the marker. Run ./setup-dashboard.sh, which undoes the change first, from its backup.

The dashboard never gets ready.
  Check ./setup-dashboard.sh --no-questions, the keys volume with
    docker run --rm --network none -v minerbot-dashboard-keys:/k mcr.microsoft.com/dotnet/runtime:10.0 ls -ln /k
  and what listens on its port: ss -ltnp 'sport = :5080'

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
  /etc/nginx/conf.d/minerbot-dashboard.conf exists: nginx would refuse every later reload, AutoSSL's included.


Backups
-------

Every night at 03:17, ./backup.sh nightly dumps MinerBot's own database into /srv/backups/minerbot and keeps 14 days of
dumps, the newest 3 always. A deploy with migrations dumps first too. Session rows are left out. backup.log has a line
for each run. A failure is mailed to root, and the 03:47 check reports a backup that is old, failed or never finished.
./status.sh --mail-test proves the mail arrives. The website's database isn't in these dumps.

To restore one:
  1. ./backup.sh manual, a dump of the current state first.
  2. docker compose stop dashboard, and the bot too once it uses this database.
  3. gzip -t <file>, and gzip -dc <file> | tail -n 1 must show: -- Dump completed
  4. To look first, in a database of its own:
       mariadb -e 'CREATE DATABASE mbrestorecheck'
       gzip -dc <file> | mariadb mbrestorecheck
       mariadb mbrestorecheck -e 'SELECT version, name FROM schema_version'
       mariadb -e 'DROP DATABASE mbrestorecheck'
     The name has no _, so cPanel never maps it to an account.
  5. gzip -dc <file> | mariadb --default-character-set=utf8mb4 <BOT_DB from server.conf>
     This replaces each table in the dump. Sessions come back empty, so everyone signs in again, which is intended.
  6. docker compose up -d --no-build --no-deps dashboard, then ./status.sh


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

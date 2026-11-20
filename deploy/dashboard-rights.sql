-- Everything the web dashboard's database login may do, and nothing else. From deploy/dashboard-rights.sql in the
-- MinerBot repository; deploy.sh installs it as /opt/minerbot/dashboard-rights.sql.
-- deploy.sh applies it as root before every dashboard swap, after the migrations; for a dashboard older than the
-- installed files, it applies the rights the last apply used instead (the copy below, or this file before any apply
-- kept one), with the GRANT lines of that dashboard's own file they lack. Either way it also adds the GRANT lines that
-- the dashboards a failed swap or rollback.sh can put back (the one that runs, :current and :previous) have in their
-- own file and the rights lack. Each apply that passes keeps a copy of what
-- it applied as /opt/minerbot/state/dashboard-rights.applied.sql, which every setup-dashboard.sh run applies again;
-- setup applies this file only before any apply has kept one. They fill in {{SITE_DB}} and {{BOT_DB}} from
-- /opt/minerbot/server.conf and {{GRANTEES}} with the login on both its hosts, after a rehearsal on a locked throwaway
-- login. The real names never go into this file.
-- Declarative: the login loses every right it has, then gets exactly these, so a right added by hand never lasts.
-- Table and column rights only: a right on a whole database would also cover tables added later, and MariaDB reads _
-- in a database name there as a wildcard.
-- A pull request whose code needs another right adds it here, after the migration that makes its table (MariaDB refuses
-- a right on a table that doesn't exist yet), and changes DashboardRightsTests. A right is removed only in a later pull
-- request than the one whose code stopped using it, so rollback.sh's previous image still has what it needs.
-- Never give this login rights in cPanel: cPanel only knows whole databases.
-- The same rules as a migration: a statement ends with a semicolon at the end of a line, and a comment sits on a line of
-- its own.

REVOKE ALL PRIVILEGES, GRANT OPTION FROM {{GRANTEES}};

-- At most 15 connections, the dashboard's two pools of 6 and room for the readiness check, and no statement longer
-- than 20 seconds, which also ends one that MySql.Data stopped waiting for after its own 15
ALTER USER {{GRANTEES}} WITH MAX_USER_CONNECTIONS 15 MAX_STATEMENT_TIME 20;

-- The website's database. This one column came first, so the readiness check could open the database, which MariaDB
-- allows a login only with a right in it. Armor review's rights below do that now, but a right goes only in a later
-- pull request than the one that stopped needing it, and it holds nothing private.
GRANT SELECT (`guild_id`) ON `{{SITE_DB}}`.`ihvph_discord_servers` TO {{GRANTEES}};

-- MinerBot's own database: the sign-in sessions, and the audit log, which this login adds rows to and reads back, but
-- can never change or delete. That holds for this login only: the bot's own login has every right in this database,
-- and while it's still a cPanel database, as before move-database.sh, the cPanel account that owns it can take it
-- over, then change or delete audit rows and sessions. apply_rights refuses while this database holds a trigger, event
-- or routine, which would act with its maker's rights on every row this login adds, and which no migration makes.
GRANT SELECT, INSERT, UPDATE, DELETE ON `{{BOT_DB}}`.`dashboard_sessions` TO {{GRANTEES}};
GRANT INSERT ON `{{BOT_DB}}`.`audit_log` TO {{GRANTEES}};

-- The audit page reads the log back, and so do each blacklist entry's history, the Overview's latest changes and each
-- server's page. On a line of its own, so the INSERT line above stays word for word what older dashboards' own files
-- have, which deploy.sh compares statement by statement.
GRANT SELECT ON `{{BOT_DB}}`.`audit_log` TO {{GRANTEES}};

-- Armor review reads every submission, whatever its status, for its list and its review page, and changes nothing but
-- the status. An approval's name check reads with FOR UPDATE, which needs no more than SELECT. The Overview reads how
-- many wait and the oldest's date. The website stays the only one that adds, edits or deletes armor.
GRANT SELECT ON `{{SITE_DB}}`.`ihvph_armor_styles` TO {{GRANTEES}};
GRANT UPDATE (`Status`) ON `{{SITE_DB}}`.`ihvph_armor_styles` TO {{GRANTEES}};

-- Each status change adds its row to the armor's review history, in the same transaction, and the review page reads the
-- history back. A row is never changed or deleted.
GRANT SELECT, INSERT ON `{{BOT_DB}}`.`armor_reviews` TO {{GRANTEES}};

-- The blacklist's pages list and search every entry, add one, change its name, reason, end date or notes, and remove it,
-- each change in one transaction with its audit row. The locked read before a change needs no more than SELECT. UPDATE
-- leaves out steam_id, so an edit can't move an entry to another account: a wrong number is removed and added again.
GRANT SELECT, INSERT, DELETE ON `{{SITE_DB}}`.`ihvph_blacklist` TO {{GRANTEES}};
GRANT UPDATE (`username`, `reason`, `expire_date`, `notes`) ON `{{SITE_DB}}`.`ihvph_blacklist` TO {{GRANTEES}};

-- The Overview and the Bot status page read the bot's status and history, which only the bot writes: its status row, its
-- minutes and days of uptime, its events, and its jobs and their runs. The servers pages read its status row and events
-- too, for how current their copy is. Nothing here changes them.
GRANT SELECT ON `{{BOT_DB}}`.`bot_status` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`uptime_samples` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`uptime_daily` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`bot_events` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`job_status` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`job_runs` TO {{GRANTEES}};

-- The servers pages read the bot's copy of its servers, which only the bot writes: the servers, their channels with the
-- bot's permissions in each, its joins and leaves, and each day's member count. The Overview reads the servers and
-- their channels too, for its banner about the settings MinerBot can't post in.
GRANT SELECT ON `{{BOT_DB}}`.`guilds` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`guild_channels` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`guild_events` TO {{GRANTEES}};
GRANT SELECT ON `{{BOT_DB}}`.`guild_daily` TO {{GRANTEES}};

-- The servers pages and that banner read each server's four channel settings from the website, which hold its ID and
-- four channel IDs, nothing private. On a line of its own, so the guild_id line above stays word for word what older
-- dashboards' own files have, which deploy.sh compares statement by statement. Only the website and the bot's /admin
-- set_channels change them.
GRANT SELECT ON `{{SITE_DB}}`.`ihvph_discord_servers` TO {{GRANTEES}};

-- The dashboard's requests to the bot. It adds a signed request and reads requests back, with how the bot answered, but
-- its INSERT names only the columns the dashboard fills in, so a request's state, run, times, result and DM state start
-- NULL and only the bot's own login writes them. Without UPDATE or DELETE it can never change or delete a row, which
-- REPLACE and ON DUPLICATE KEY UPDATE would need too.
GRANT SELECT ON `{{BOT_DB}}`.`bot_commands` TO {{GRANTEES}};
GRANT INSERT (`request_key`, `kind`, `actor_id`, `actor_name`, `actor_role`, `guild_id`, `channel_id`, `argument`, `created_at`, `expires_at`, `signature`) ON `{{BOT_DB}}`.`bot_commands` TO {{GRANTEES}};

-- The bot's pause flag and status text, which only signed requests change
GRANT SELECT ON `{{BOT_DB}}`.`bot_settings` TO {{GRANTEES}};

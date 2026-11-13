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

-- MinerBot's own database: the sign-in sessions, and adding rows to the audit log, which this login can never change or
-- read back. Reading it comes with the audit page. That holds for this login only: the bot's own login has every right
-- in this database, and while it's still a cPanel database, as before move-database.sh, the cPanel account that owns
-- it can take it over, then change or delete audit rows and sessions. apply_rights refuses while this database holds a
-- trigger, event or routine, which would act with its maker's rights on every row this login adds, and which no
-- migration makes.
GRANT SELECT, INSERT, UPDATE, DELETE ON `{{BOT_DB}}`.`dashboard_sessions` TO {{GRANTEES}};
GRANT INSERT ON `{{BOT_DB}}`.`audit_log` TO {{GRANTEES}};

-- Armor review reads every submission, whatever its status, for its list and its review page, and changes nothing but
-- the status. An approval's name check reads with FOR UPDATE, which needs no more than SELECT. The website stays the
-- only one that adds, edits or deletes armor.
GRANT SELECT ON `{{SITE_DB}}`.`ihvph_armor_styles` TO {{GRANTEES}};
GRANT UPDATE (`Status`) ON `{{SITE_DB}}`.`ihvph_armor_styles` TO {{GRANTEES}};

-- Each status change adds its row to the armor's review history, in the same transaction, and the review page reads the
-- history back. A row is never changed or deleted.
GRANT SELECT, INSERT ON `{{BOT_DB}}`.`armor_reviews` TO {{GRANTEES}};

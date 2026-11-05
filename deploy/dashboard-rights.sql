-- Everything the web dashboard's database login may do, and nothing else. From deploy/dashboard-rights.sql in the
-- MinerBot repository; deploy.sh installs it as /opt/minerbot/dashboard-rights.sql.
-- deploy.sh applies it as root before every dashboard swap, after the migrations, and setup-dashboard.sh after it creates
-- the login or changes its password. They fill in {{SITE_DB}} and {{BOT_DB}} from /opt/minerbot/server.conf and
-- {{GRANTEES}} with the login on both its hosts, after a rehearsal on a locked throwaway login. The real names never go
-- into this file.
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

-- The website's database: nothing is read there yet, but MariaDB lets a login open a database only with a right in it,
-- and the readiness check opens it. One column that holds nothing private.
GRANT SELECT (`guild_id`) ON `{{SITE_DB}}`.`ihvph_discord_servers` TO {{GRANTEES}};

-- MinerBot's own database: the sign-in sessions, and adding rows to the audit log, which the login can never change or
-- read back. Reading it comes with the audit page.
GRANT SELECT, INSERT, UPDATE, DELETE ON `{{BOT_DB}}`.`dashboard_sessions` TO {{GRANTEES}};
GRANT INSERT ON `{{BOT_DB}}`.`audit_log` TO {{GRANTEES}};

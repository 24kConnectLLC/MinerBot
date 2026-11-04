-- 0001_core: the first tables in MinerBot's own database, for the dashboard's sign-in, armor review and audit log.
-- The migration runner splits this file at lines that end with a semicolon and skips lines that start with two
-- dashes, so a semicolon may only end a statement, and comments sit on lines of their own.
-- Every statement is safe to run again. MariaDB commits each CREATE TABLE as it goes, so a run that stops halfway
-- leaves this file unrecorded, and the next run starts it from the top.
-- Times are UTC with milliseconds, and the code passes them in. Discord IDs are BIGINT UNSIGNED. Armor IDs are
-- BIGINT, signed like ihvph_armor_styles.ID. Role, outcome and status words are plain text checked by constants in
-- code, so a new word needs no change here. The collation is the website tables' own, so columns from both
-- databases can be compared in one query without a collation error.

-- Every change made through the dashboard, and every refusal. Rows are only ever added.
CREATE TABLE IF NOT EXISTS audit_log (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  at DATETIME(3) NOT NULL,
  -- Who did it: a Discord user ID, or NULL for the bot itself, with the name as it was at the time
  actor_id BIGINT UNSIGNED NULL,
  actor_name VARCHAR(100) NULL,
  actor_role VARCHAR(20) NOT NULL,
  -- What was done, such as armor.approve, and how it went: ok, failed, refused or unknown
  action VARCHAR(50) NOT NULL,
  outcome VARCHAR(10) NOT NULL,
  guild_id BIGINT UNSIGNED NULL,
  target_type VARCHAR(30) NULL,
  target_id VARCHAR(40) NULL,
  -- Only the fields that changed, and never a secret
  before_json TEXT NULL,
  after_json TEXT NULL,
  detail VARCHAR(300) NULL,
  ip VARCHAR(45) NULL,
  request_id VARCHAR(64) NULL,
  PRIMARY KEY (id),
  KEY audit_log_at (at),
  KEY audit_log_actor (actor_id, at),
  KEY audit_log_guild (guild_id, at),
  KEY audit_log_action (action, at),
  KEY audit_log_target (target_type, target_id, at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Signed-in dashboard sessions. The cookie holds a random key, and only the SHA-256 hash of it is kept here.
-- The dashboard encrypts the ticket before it arrives.
CREATE TABLE IF NOT EXISTS dashboard_sessions (
  id_hash BINARY(32) NOT NULL,
  user_id BIGINT UNSIGNED NOT NULL,
  user_name VARCHAR(100) NOT NULL,
  created_at DATETIME(3) NOT NULL,
  last_seen_at DATETIME(3) NOT NULL,
  -- The latest a session can last, whatever its activity
  expires_at DATETIME(3) NOT NULL,
  ip VARCHAR(45) NULL,
  user_agent VARCHAR(200) NULL,
  ticket MEDIUMBLOB NOT NULL,
  PRIMARY KEY (id_hash),
  KEY dashboard_sessions_user (user_id),
  KEY dashboard_sessions_expires (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Who changed an armor's status, when and why. The armor is in the website's database, so there's no foreign key.
CREATE TABLE IF NOT EXISTS armor_reviews (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  armor_id BIGINT NOT NULL,
  -- NULL when the armor had no status
  from_status VARCHAR(20) NULL,
  to_status VARCHAR(20) NOT NULL,
  -- Optional, and never shown to the submitter
  reason VARCHAR(500) NULL,
  reviewed_by BIGINT UNSIGNED NOT NULL,
  reviewer_name VARCHAR(100) NULL,
  reviewed_at DATETIME(3) NOT NULL,
  PRIMARY KEY (id),
  KEY armor_reviews_armor (armor_id, reviewed_at),
  KEY armor_reviews_at (reviewed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- 0002_bot_status: the bot's status and history, which only the bot writes, with its own login, and the dashboard's
-- status pages read. The same rules as 0001_core: a semicolon may only end a statement, comments sit on lines of their
-- own, and every statement is safe to run again.
-- Times are UTC, and the code passes them in, never the server's clock, which is US Eastern on the live server.
-- States, kinds, levels and results are plain text checked by constants in code. Nothing here holds a secret: no token,
-- no SteamID, no account name and no message from a library, only the bot's own wording.

-- Exactly one row, id 1, which the bot rewrites while it runs. run_id is random for each start of the process, and
-- stopped_at is set by a clean stop, so a row of an earlier run without it is a run that ended without one.
CREATE TABLE IF NOT EXISTS bot_status (
  id TINYINT UNSIGNED NOT NULL,
  run_id BIGINT UNSIGNED NOT NULL,
  -- The commit the image was built from, or NULL when the image has none
  version VARCHAR(40) NULL,
  started_at DATETIME(3) NOT NULL,
  -- When the row was last written
  heartbeat_at DATETIME(3) NOT NULL,
  stopped_at DATETIME(3) NULL,
  -- connected, connecting or disconnected, and since when
  discord_state VARCHAR(20) NOT NULL,
  discord_since DATETIME(3) NULL,
  discord_latency_ms INT NULL,
  discord_guilds INT NULL,
  -- logged_on, offline or needs_sign_in, and since when
  steam_state VARCHAR(20) NOT NULL,
  steam_since DATETIME(3) NULL,
  -- Steam's last answer to a logon, an EResult name, while the bot isn't logged on
  steam_last_result VARCHAR(40) NULL,
  steam_token_expires DATE NULL,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- One row for each minute the bot ran: the minute's start, with whether Discord and Steam were up then. A minute
-- without a row is one the bot didn't run, or one whose sample it couldn't write: it keeps 6 hours of them while
-- MinerBot's database is down, records.dropped counts the older ones it let go, and a stop before the database is back
-- loses the rest.
CREATE TABLE IF NOT EXISTS uptime_samples (
  minute DATETIME NOT NULL,
  discord_up TINYINT(1) NOT NULL,
  steam_up TINYINT(1) NOT NULL,
  PRIMARY KEY (minute)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Each UTC day's totals, which outlive the minutes they were counted from
CREATE TABLE IF NOT EXISTS uptime_daily (
  day DATE NOT NULL,
  sampled_minutes SMALLINT UNSIGNED NOT NULL,
  discord_minutes SMALLINT UNSIGNED NOT NULL,
  steam_minutes SMALLINT UNSIGNED NOT NULL,
  PRIMARY KEY (day)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- What happened, such as discord.disconnected, with a detail in the bot's own wording. seq counts from 1 in each run,
-- so an event that's written again lands once.
CREATE TABLE IF NOT EXISTS bot_events (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  run_id BIGINT UNSIGNED NOT NULL,
  seq INT UNSIGNED NOT NULL,
  at DATETIME(3) NOT NULL,
  kind VARCHAR(40) NOT NULL,
  -- info, warning or error
  level VARCHAR(10) NOT NULL,
  detail VARCHAR(300) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY bot_events_run (run_id, seq),
  KEY bot_events_at (at),
  KEY bot_events_kind (kind, at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Each scheduled job, named <Class>.<Method> as the logs name it, with its schedule (cron, which the bot reads in US
-- Eastern time), when it next runs, and how its last run went
CREATE TABLE IF NOT EXISTS job_status (
  job VARCHAR(100) NOT NULL,
  schedule VARCHAR(100) NOT NULL,
  next_run_at DATETIME(3) NULL,
  last_started_at DATETIME(3) NULL,
  last_ended_at DATETIME(3) NULL,
  -- ok, failed, cut_off or skipped
  last_result VARCHAR(20) NULL,
  last_duration_ms INT UNSIGNED NULL,
  last_detail VARCHAR(300) NULL,
  updated_at DATETIME(3) NOT NULL,
  PRIMARY KEY (job)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Every run of a job. A run that was cut off stays cut_off, and gets its end when it ends.
CREATE TABLE IF NOT EXISTS job_runs (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  run_id BIGINT UNSIGNED NOT NULL,
  seq INT UNSIGNED NOT NULL,
  job VARCHAR(100) NOT NULL,
  started_at DATETIME(3) NOT NULL,
  ended_at DATETIME(3) NULL,
  result VARCHAR(20) NOT NULL,
  duration_ms INT UNSIGNED NULL,
  detail VARCHAR(300) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY job_runs_run (run_id, seq),
  KEY job_runs_job (job, started_at),
  KEY job_runs_started (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

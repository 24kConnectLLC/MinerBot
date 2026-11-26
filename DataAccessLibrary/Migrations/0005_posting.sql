-- 0005_posting: each server's switches for MinerBot's armor, map and lobby posts, what the bot has found and what each
-- server is still owed, one row for each message it sent or couldn't send, each server's delivery status, the daily
-- totals, and the open lobbies over time, with the run of the bot that keeps its posts this way. The same rules as
-- 0001_core: a semicolon may only end a statement, comments sit on lines of their own, and every statement is safe to
-- run again. Times are UTC with milliseconds that the code passes in, never the server's clock, which is US Eastern on
-- the live server. Discord, Steam and armor IDs are BIGINT UNSIGNED. Sources, states and reasons are plain text checked
-- by constants in code. There's no DEFAULT anywhere. Nothing here holds a name, a message's text or a player's ID.

-- Each server's switches, which the dashboard writes. No row means every kind is on. The bot reads every row at each
-- notification run and deletes a server's row when the server removes MinerBot. version grows by one with each save,
-- so a form drawn before another save is refused.
CREATE TABLE IF NOT EXISTS guild_settings (
  guild_id BIGINT UNSIGNED NOT NULL,
  armor_on TINYINT(1) NOT NULL,
  maps_on TINYINT(1) NOT NULL,
  lobbies_on TINYINT(1) NOT NULL,
  version INT UNSIGNED NOT NULL,
  changed_at DATETIME(3) NOT NULL,
  -- A Discord user ID, and owner or server_admin
  changed_by BIGINT UNSIGNED NOT NULL,
  changed_by_role VARCHAR(20) NOT NULL,
  PRIMARY KEY (guild_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- One row for each source the bot watches: armor, maps, lobbies and submissions. A baseline is a source's first good
-- read, whose items the bot takes as already posted, or a new one taken when an older bot posted meanwhile. checked_at
-- is the start of the last run that read the source and saved what it found.
CREATE TABLE IF NOT EXISTS notification_baseline (
  source VARCHAR(20) NOT NULL,
  taken_at DATETIME(3) NOT NULL,
  taken_by_run BIGINT UNSIGNED NOT NULL,
  -- first or older_bot
  reason VARCHAR(20) NOT NULL,
  item_count INT UNSIGNED NOT NULL,
  -- Maps only, by Steam's clock: the oldest update of the baseline's read
  baseline_time DATETIME(3) NULL,
  -- Submissions only: the highest armor ID of any status at the first look, below which no armor is new, and when the
  -- bot last tried to tell the owner about new ones
  high_id BIGINT UNSIGNED NULL,
  alerted_at DATETIME(3) NULL,
  checked_at DATETIME(3) NOT NULL,
  checked_by_run BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (source)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Each item the bot has found: an approved armor, a Workshop map, an open lobby or a pending submission, by its own ID.
-- baseline, queued, ignored, alerted or alert_failed. A map's updated_at is Steam's time of the update last recorded,
-- and queued_at the bot's time it was last queued, for the 24-hour cooldown. last_seen_at is a lobby's last read,
-- refreshed at most hourly, which the clean-up goes by.
CREATE TABLE IF NOT EXISTS notification_seen (
  source VARCHAR(20) NOT NULL,
  item_id BIGINT UNSIGNED NOT NULL,
  state VARCHAR(20) NOT NULL,
  first_seen_at DATETIME(3) NOT NULL,
  last_seen_at DATETIME(3) NOT NULL,
  updated_at DATETIME(3) NULL,
  queued_at DATETIME(3) NULL,
  PRIMARY KEY (source, item_id),
  KEY notification_seen_last (source, last_seen_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- What each server is owed, and what became of it: one row for each item and each server that had its kind on when the
-- bot found it. revision is 0 for armor and lobbies, and for a map the update it's for, as Steam's Unix time. pending,
-- then sending once a message claims it, then sent, or dropped with a reason. A row goes back from sending to pending
-- only when its message is known not to have been made.
CREATE TABLE IF NOT EXISTS notification_queue (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  guild_id BIGINT UNSIGNED NOT NULL,
  source VARCHAR(20) NOT NULL,
  item_id BIGINT UNSIGNED NOT NULL,
  revision BIGINT UNSIGNED NOT NULL,
  is_new TINYINT(1) NOT NULL,
  queued_at DATETIME(3) NOT NULL,
  state VARCHAR(10) NOT NULL,
  delivery_id BIGINT UNSIGNED NULL,
  reason VARCHAR(30) NULL,
  settled_at DATETIME(3) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY notification_queue_item (guild_id, source, item_id, revision),
  KEY notification_queue_open (state, queued_at),
  KEY notification_queue_lookup (source, item_id),
  KEY notification_queue_delivery (delivery_id),
  KEY notification_queue_settled (settled_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- One row for each message the bot sent or tried to send for its armor, map and lobby posts, written before the send as
-- sending, then sent with Discord's message ID, failed with a reason and Discord's numbers, in_doubt while Discord
-- didn't answer, or unconfirmed when the bot couldn't tell whether it arrived and counts it as sent. A refusal the
-- bot's cache saw first is a failed row with no send. attempt_key is the bot's own random number for the attempt, so a
-- write it repeats lands once. missing: 1 View Channel, 2 Send Messages, 4 Embed Links, 8 Read Message History.
CREATE TABLE IF NOT EXISTS notification_deliveries (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  attempt_key BIGINT UNSIGNED NOT NULL,
  run_id BIGINT UNSIGNED NOT NULL,
  run_started_at DATETIME(3) NOT NULL,
  guild_id BIGINT UNSIGNED NOT NULL,
  channel_id BIGINT UNSIGNED NOT NULL,
  source VARCHAR(20) NOT NULL,
  part TINYINT UNSIGNED NOT NULL,
  item_count SMALLINT UNSIGNED NOT NULL,
  attempted_at DATETIME(3) NOT NULL,
  state VARCHAR(12) NOT NULL,
  message_id BIGINT UNSIGNED NULL,
  settled_at DATETIME(3) NULL,
  reason VARCHAR(30) NULL,
  missing TINYINT UNSIGNED NULL,
  http_status SMALLINT UNSIGNED NULL,
  discord_code INT UNSIGNED NULL,
  -- How many times the bot looked for an in_doubt message in the channel's history
  checks TINYINT UNSIGNED NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY notification_deliveries_attempt (attempt_key),
  KEY notification_deliveries_guild (guild_id, attempted_at),
  KEY notification_deliveries_state (state, attempted_at),
  KEY notification_deliveries_run (run_started_at),
  KEY notification_deliveries_at (attempted_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Each server's delivery of each kind as its last message or refusal left it: the last that went, the last that didn't
-- and why, since when posts keep failing and how many tries in a row failed, and the wait before the next try.
-- waiting_on is a hash of what the wait started from, the channel, the switch and the bot's permissions there, so a
-- change ends it.
CREATE TABLE IF NOT EXISTS guild_delivery_status (
  guild_id BIGINT UNSIGNED NOT NULL,
  source VARCHAR(20) NOT NULL,
  last_sent_at DATETIME(3) NULL,
  last_sent_channel_id BIGINT UNSIGNED NULL,
  last_message_id BIGINT UNSIGNED NULL,
  last_failed_at DATETIME(3) NULL,
  last_failed_channel_id BIGINT UNSIGNED NULL,
  last_reason VARCHAR(30) NULL,
  last_missing TINYINT UNSIGNED NULL,
  last_http_status SMALLINT UNSIGNED NULL,
  last_discord_code INT UNSIGNED NULL,
  failing_since DATETIME(3) NULL,
  failures INT UNSIGNED NOT NULL,
  next_try_at DATETIME(3) NULL,
  waiting_on BIGINT UNSIGNED NULL,
  updated_at DATETIME(3) NOT NULL,
  PRIMARY KEY (guild_id, source),
  KEY guild_delivery_status_failing (failing_since)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- Each UTC day's messages and items by source, outcome and reason, totalled each night from the delivery log and the
-- queue while they still hold the day, so a day is worked out again from all of it. reason is empty for what went.
CREATE TABLE IF NOT EXISTS delivery_daily (
  day DATE NOT NULL,
  source VARCHAR(20) NOT NULL,
  outcome VARCHAR(12) NOT NULL,
  reason VARCHAR(30) NOT NULL,
  messages INT UNSIGNED NOT NULL,
  items INT UNSIGNED NOT NULL,
  PRIMARY KEY (day, source, outcome, reason)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- The open public lobbies at each notification run's read, by its UTC minute: counts only, never a host, a lobby or a
-- map. steam_answered 0 is a read Steam gave no answer to, and then every count is NULL, so an outage never reads as an
-- empty game.
CREATE TABLE IF NOT EXISTS lobby_samples (
  minute DATETIME NOT NULL,
  steam_answered TINYINT(1) NOT NULL,
  lobbies SMALLINT UNSIGNED NULL,
  players SMALLINT UNSIGNED NULL,
  slots SMALLINT UNSIGNED NULL,
  current_lobbies SMALLINT UNSIGNED NULL,
  current_players SMALLINT UNSIGNED NULL,
  full_lobbies SMALLINT UNSIGNED NULL,
  gamenight_lobbies SMALLINT UNSIGNED NULL,
  official_lobbies SMALLINT UNSIGNED NULL,
  PRIMARY KEY (minute)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- The run of a bot that keeps its posts in notification_queue, as run_id. An older bot doesn't know the column and
-- leaves it as it was, so a row whose run_id differs was written by a bot that keeps them only in memory.
ALTER TABLE bot_status ADD COLUMN IF NOT EXISTS posting_run_id BIGINT UNSIGNED NULL;

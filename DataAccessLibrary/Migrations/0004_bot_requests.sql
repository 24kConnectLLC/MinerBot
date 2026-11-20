-- 0004_bot_requests: the dashboard's signed requests to the bot, and the bot's settings that only those requests change.
-- The same rules as 0001_core: a semicolon may only end a statement, comments sit on lines of their own, and every
-- statement is safe to run again. Times are UTC with milliseconds that the code passes in, never the server's clock,
-- which is US Eastern on the live server. Discord IDs are BIGINT UNSIGNED. Kinds, roles and states are plain text
-- checked by the request list in MinerBot.Shared. There's no DEFAULT anywhere, because the dashboard's login may name
-- only a request's own columns, so every column the bot fills in starts NULL. Nothing here is secret: a signature is a
-- check, not a key, and no row holds a visitor's address.

-- One row for each request the dashboard sends the bot. The dashboard adds a row and never changes it. The bot decides
-- refused or expired without taking it, or takes it by moving state from NULL to running in one conditional UPDATE,
-- then writes done, failed or unknown with a result in its own words. A row one run took is never acted on by another.
CREATE TABLE IF NOT EXISTS bot_commands (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  -- 32 hexadecimal digits in small letters that the dashboard makes at random and signs, so a copied row is refused
  request_key CHAR(32) NOT NULL,
  -- One word of the fixed list, such as posts.pause
  kind VARCHAR(40) NOT NULL,
  -- Who asked: a Discord user ID, the name the dashboard had for them, and owner, admin or server_admin
  actor_id BIGINT UNSIGNED NOT NULL,
  actor_name VARCHAR(100) NOT NULL,
  actor_role VARCHAR(20) NOT NULL,
  -- The server and the channel a request is about, NULL for the others
  guild_id BIGINT UNSIGNED NULL,
  channel_id BIGINT UNSIGNED NULL,
  -- The request's one text: a pause's reason, the status text or a custom profile link's name
  argument VARCHAR(200) NULL,
  -- The dashboard's clock, to the millisecond. The bot takes a request only before expires_at, by its own clock.
  created_at DATETIME(3) NOT NULL,
  expires_at DATETIME(3) NOT NULL,
  -- HMAC-SHA256 of the fields above, in the layout of MinerBot.Shared's RequestSigning
  signature BINARY(32) NOT NULL,
  -- The bot's columns, NULL until it writes them. state is NULL while the request waits.
  state VARCHAR(10) NULL,
  -- The run of the bot that decided or took the request, as bot_status.run_id, and when it took it
  run_id BIGINT UNSIGNED NULL,
  claimed_at DATETIME(3) NULL,
  ended_at DATETIME(3) NULL,
  -- In the bot's own words, never a library's message and never the request's own text
  result VARCHAR(300) NULL,
  -- A number the request returns: a looked-up account number, or a test message's ID
  result_number BIGINT UNSIGNED NULL,
  -- For the kinds that tell the owner: sent or failed, once the direct message went or didn't
  dm_state VARCHAR(10) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY bot_commands_key (request_key),
  KEY bot_commands_state (state, id),
  KEY bot_commands_kind (kind, claimed_at),
  KEY bot_commands_guild (guild_id, kind, claimed_at),
  -- The nightly clean-up's: a request goes by when it was made, or when the bot ended it if that came first
  KEY bot_commands_created (created_at),
  KEY bot_commands_ended (ended_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- The bot's settings that only signed requests change: whether its armor, map and lobby posts are paused and why, and
-- its status text, NULL for the default. One row, id 1, which only the bot writes, the first time either changes, so no
-- row means not paused, with the default status. Each part keeps who changed it last, when, and on which request.
CREATE TABLE IF NOT EXISTS bot_settings (
  id TINYINT UNSIGNED NOT NULL,
  posts_paused TINYINT(1) NOT NULL,
  -- Only while paused
  pause_reason VARCHAR(200) NULL,
  pause_changed_at DATETIME(3) NULL,
  pause_changed_by BIGINT UNSIGNED NULL,
  pause_request_id BIGINT UNSIGNED NULL,
  status_text VARCHAR(128) NULL,
  status_changed_at DATETIME(3) NULL,
  status_changed_by BIGINT UNSIGNED NULL,
  status_request_id BIGINT UNSIGNED NULL,
  updated_at DATETIME(3) NOT NULL,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

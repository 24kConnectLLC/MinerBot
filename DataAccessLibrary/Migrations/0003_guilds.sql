-- 0003_guilds: the servers the bot is in or has left, their text and announcement channels with the bot's permissions in
-- each, the bot's joins and leaves, and each server's member count once a day. Only the bot writes them, with its own
-- login, and the dashboard's servers pages will read them. The same rules as 0001_core: a semicolon may only end a
-- statement, comments sit on lines of their own, and every statement is safe to run again.
-- Times are UTC, and the code passes them in, never the server's clock, which is US Eastern on the live server. Discord
-- IDs are BIGINT UNSIGNED. Kinds and channel types are plain text checked by constants in code. Server and channel names
-- are Discord's, and nothing here holds a secret, an owner or a member.

-- One row for each server the bot has been in, kept after it leaves. left_at is NULL while the bot is in it.
CREATE TABLE IF NOT EXISTS guilds (
  guild_id BIGINT UNSIGNED NOT NULL,
  name VARCHAR(100) NOT NULL,
  -- For Discord's CDN, NULL when the server has no icon
  icon_hash VARCHAR(64) NULL,
  -- Discord's count when the bot first saw the server or came back to it, then its approximate count once a day
  member_count INT UNSIGNED NULL,
  -- When the bot joined, as Discord says
  bot_joined_at DATETIME(3) NULL,
  first_seen_at DATETIME(3) NOT NULL,
  left_at DATETIME(3) NULL,
  -- 0 while Discord reports the server unavailable, in an outage
  available TINYINT(1) NOT NULL,
  -- The bot's server-wide permissions, as Discord's raw value of its roles' permissions
  bot_administrator TINYINT(1) NOT NULL,
  bot_permissions BIGINT UNSIGNED NOT NULL,
  updated_at DATETIME(3) NOT NULL,
  PRIMARY KEY (guild_id),
  KEY guilds_left (left_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- One row for each text or announcement channel of each server the bot is in, with whether the bot can view it, send
-- messages and embed links there, from its roles and the channel's overwrites. A channel that goes is deleted, and so
-- is every channel of a server the bot leaves. updated_at is when the bot last wrote its server's channels.
CREATE TABLE IF NOT EXISTS guild_channels (
  channel_id BIGINT UNSIGNED NOT NULL,
  guild_id BIGINT UNSIGNED NOT NULL,
  name VARCHAR(100) NOT NULL,
  -- text or announcement
  type VARCHAR(20) NOT NULL,
  -- Its category's name, ID and position, NULL outside one. Discord lists the channels outside a category first, then
  -- each category by its position, with its channels by theirs, the lower ID first on a tie.
  category VARCHAR(100) NULL,
  category_id BIGINT UNSIGNED NULL,
  category_position INT NULL,
  `position` INT NOT NULL,
  can_view TINYINT(1) NOT NULL,
  can_send TINYINT(1) NOT NULL,
  can_embed TINYINT(1) NOT NULL,
  updated_at DATETIME(3) NOT NULL,
  PRIMARY KEY (channel_id),
  KEY guild_channels_guild (guild_id, `position`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- The bot joining and leaving servers, with a detail in the bot's own wording, never a server's name. A join's time is
-- when Discord says the bot joined. seq numbers a run's joins and leaves in order from 1. A write that failed may have
-- used its number all the same, so that number isn't given again, and a run's numbers can have gaps. A join or leave
-- that's written again is recorded once, because the server's row in guilds decides it.
CREATE TABLE IF NOT EXISTS guild_events (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  run_id BIGINT UNSIGNED NOT NULL,
  seq INT UNSIGNED NOT NULL,
  guild_id BIGINT UNSIGNED NOT NULL,
  at DATETIME(3) NOT NULL,
  -- joined or left
  kind VARCHAR(20) NOT NULL,
  detail VARCHAR(300) NULL,
  PRIMARY KEY (id),
  UNIQUE KEY guild_events_run (run_id, seq),
  KEY guild_events_guild (guild_id, at),
  KEY guild_events_at (at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- One row a UTC day for each server the bot is in, with Discord's approximate member count, for the members' trend
CREATE TABLE IF NOT EXISTS guild_daily (
  day DATE NOT NULL,
  guild_id BIGINT UNSIGNED NOT NULL,
  member_count INT UNSIGNED NULL,
  PRIMARY KEY (day, guild_id),
  KEY guild_daily_guild (guild_id, day)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

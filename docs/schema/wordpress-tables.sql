-- The website tables MinerBot uses. The first three are as SHOW CREATE TABLE gave them on 4 November 2026, without
-- their rows or the AUTO_INCREMENT counter. They belong to the WordPress site's database: the bot and the dashboard read
-- and change their rows but never their structure. The database tests build their copy of the site's tables from this
-- file.

CREATE TABLE `ihvph_armor_styles` (
  `ID` bigint(20) NOT NULL AUTO_INCREMENT,
  `CreatedAt` datetime NOT NULL DEFAULT current_timestamp(),
  `UserID` bigint(20) DEFAULT NULL,
  `Status` varchar(20) DEFAULT NULL,
  `Author` varchar(60) NOT NULL,
  `ArmorName` varchar(60) DEFAULT NULL,
  `ArmorDescription` varchar(256) DEFAULT NULL,
  `ArmorScreenshot` longtext DEFAULT NULL,
  `ArmorChoice` varchar(60) DEFAULT NULL,
  `ArmorSkin` varchar(60) DEFAULT NULL,
  `Helmet` int(3) DEFAULT NULL,
  `Visor` int(3) DEFAULT NULL,
  `Torso` int(3) DEFAULT NULL,
  `Shoulders` int(3) DEFAULT NULL,
  `Biceps` int(3) DEFAULT NULL,
  `Forearms` int(3) DEFAULT NULL,
  `Hands` int(3) DEFAULT NULL,
  `Pelvis` int(3) DEFAULT NULL,
  `Thighs` int(3) DEFAULT NULL,
  `Legs` int(3) DEFAULT NULL,
  `Boots` int(3) DEFAULT NULL,
  `Shields` int(3) DEFAULT NULL,
  `Other` int(3) DEFAULT NULL,
  `ColorArray` varchar(20) DEFAULT NULL,
  PRIMARY KEY (`ID`),
  KEY `Armor Name` (`ArmorName`),
  KEY `idx_ihvph_armor_styles_ArmorChoice` (`ArmorChoice`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

CREATE TABLE `ihvph_blacklist` (
  `steam_id` decimal(10,0) NOT NULL,
  `username` varchar(60) DEFAULT NULL,
  `reason` varchar(60) DEFAULT NULL,
  `expire_date` datetime DEFAULT NULL,
  `notes` varchar(120) DEFAULT NULL,
  PRIMARY KEY (`steam_id`),
  UNIQUE KEY `steam_id_UNIQUE` (`steam_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

CREATE TABLE `ihvph_discord_servers` (
  `guild_id` bigint(20) unsigned NOT NULL,
  `armor_channel_id` bigint(20) unsigned NOT NULL,
  `map_channel_id` bigint(20) unsigned NOT NULL,
  `lobby_channel_id` bigint(20) unsigned NOT NULL,
  `announcements_channel_id` bigint(20) unsigned NOT NULL,
  PRIMARY KEY (`guild_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;

-- The three tables of the feed MinerBot writes for the website's Lobbies and Maps pages, as the website's own migration,
-- app/migrations/002_bot_feed.sql in its repository, makes them. They belong to the website too: it makes them and only
-- reads them, and the bot only writes their rows. A change to them is made there first, and copied here.

-- The public lobbies that are open right now. Each good read of Steam replaces the whole set: lobbies that
-- are still open keep their first_seen, new ones are added, closed ones are deleted.
CREATE TABLE IF NOT EXISTS mmh_feed_lobbies (
  lobby_id BIGINT UNSIGNED NOT NULL,
  owner_id BIGINT UNSIGNED NOT NULL,
  host_name VARCHAR(64) NOT NULL DEFAULT '',
  host_kind VARCHAR(16) NOT NULL DEFAULT 'player',
  map_name VARCHAR(128) NOT NULL DEFAULT '',
  modes VARCHAR(255) NOT NULL DEFAULT '',
  version_number INT UNSIGNED NOT NULL DEFAULT 0,
  version_label VARCHAR(64) NOT NULL DEFAULT '',
  is_current TINYINT NOT NULL DEFAULT 0,
  players SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  max_players SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  first_seen DATETIME NOT NULL,
  last_seen DATETIME NOT NULL,
  PRIMARY KEY (lobby_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- The newest and most recently updated public maps of the Steam Workshop, one row a map, kept as they
-- drop out of Steam's latest list so the page has a history.
CREATE TABLE IF NOT EXISTS mmh_feed_maps (
  file_id BIGINT UNSIGNED NOT NULL,
  title VARCHAR(255) NOT NULL DEFAULT '',
  description TEXT NULL,
  preview_url VARCHAR(500) NOT NULL DEFAULT '',
  creator_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
  tags VARCHAR(500) NOT NULL DEFAULT '',
  file_size BIGINT UNSIGNED NOT NULL DEFAULT 0,
  subscriptions INT UNSIGNED NOT NULL DEFAULT 0,
  lifetime_subscriptions INT UNSIGNED NOT NULL DEFAULT 0,
  favorited INT UNSIGNED NOT NULL DEFAULT 0,
  lifetime_favorited INT UNSIGNED NOT NULL DEFAULT 0,
  views INT UNSIGNED NOT NULL DEFAULT 0,
  time_created DATETIME NOT NULL,
  time_updated DATETIME NOT NULL,
  first_seen DATETIME NOT NULL,
  last_seen DATETIME NOT NULL,
  PRIMARY KEY (file_id),
  KEY time_updated (time_updated),
  KEY time_created (time_created)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- One row a feed, 'lobbies' and 'maps': when MinerBot last tried Steam, and when it last got an answer and
-- wrote it. The pages call a feed offline when ok_at is too old.
CREATE TABLE IF NOT EXISTS mmh_feed_state (
  feed VARCHAR(16) NOT NULL,
  checked_at DATETIME NOT NULL,
  ok_at DATETIME NULL,
  item_count INT UNSIGNED NOT NULL DEFAULT 0,
  PRIMARY KEY (feed)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

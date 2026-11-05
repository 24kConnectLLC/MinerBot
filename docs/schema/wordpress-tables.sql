-- The three website tables MinerBot uses, as SHOW CREATE TABLE gave them on 4 November 2026, without their rows
-- or the AUTO_INCREMENT counter. They belong to the WordPress site's database: the bot and the dashboard read and
-- change their rows but never their structure. The database tests build their copy of the site's tables from this file.

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

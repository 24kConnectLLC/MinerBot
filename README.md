# MinerBot 🤖⛏️

A **Murder Miners** Discord bot built by **24k Connect LLC (24kConnect)**.

MinerBot 2.0 uses modern **Slash Commands** to streamline your Murder Miners community experience—armor lookups, workshop map discovery, lobby tracking, Steam utilities, and more.

**Website / Docs:** https://murderminershub.com/discord-bot/

---

## ✨ Features

- Slash command-based interaction (**MinerBot 2.0**)
- Armor catalog: list + lookup
- Workshop maps: latest + lookup
- Public lobby discovery
- Posts of new armor, workshop maps and lobbies in each server's channels, none lost or doubled by a restart
- A feed of the open lobbies and the latest maps for the website's pages
- Steam profile utilities (SteamID conversions)
- Admin tools: announcements + channel setup

> More features coming soon!

---

## 🧾 Commands

### Admin *(Admin Only)*

- `/admin announce [message]` — Posts an announcement of up to 2,000 characters in the server's announcements channel.
- `/admin set_channels (optional) [armor-channel] (optional) [map-channel] (optional) [lobby-channel] (optional) [announcements-channel]` — Sets where the bot posts each kind of notification. Any kind you leave out is turned off, so list every channel you want to keep.
- `/admin show_channels` — Shows each kind of notification with its channel, or `off`, and says when MinerBot's owner switched a kind off for the server on MinerBot's dashboard, so nothing is posted in its channel.

The bot needs View Channel, Send Messages and Embed Links in each channel you choose, and `/admin set_channels` saves nothing until it has them. With Read Message History there too, it can check whether a post arrived when Discord didn't answer about it ([Posts](#-posts)).

### Armor

- `/armor list (optional) [helmet-type]` — Lists all armor styles (optionally filtered by helmet type).
- `/armor lookup [armor-name]` — Finds an armor style by name (use `/armor list` to browse).
- `/armor helmets` — Lists all helmet types.

### Maps

- `/maps latest` — Returns up to **5** detailed results for the newest maps on the Murder Miners Workshop.
- `/maps lookup [map-url]` — Returns detailed info for the provided Workshop map link or ID.

### Steam / Utility

- `/more steam_id [profile-url]` — Returns all SteamID formats for the supplied Steam profile URL.

### Info

- `/help` — Lists all slash commands.
- `/more info` — Credits + more info about MinerBot.

### Lobbies

- `/murder-miners lobbies` — Finds currently **publicly open** Murder Miners lobbies.

---

## 🔗 Invite MinerBot to Your Discord Server (OAuth2)

MinerBot is installed using a Discord OAuth2 invite link with these scopes:

- `bot`
- `applications.commands` (Slash Commands)

### Recommended (least privilege)

Most servers can start with these permissions:

- View Channels
- Send Messages
- Embed Links
- Read Message History
- Pin Messages (for the `pin` message command)

**Permissions integer (recommended):** `2251799813770240`

Invite URL template:

```text
https://discord.com/api/oauth2/authorize?client_id=YOUR_CLIENT_ID&permissions=2251799813770240&scope=bot%20applications.commands
```

> Replace `YOUR_CLIENT_ID` with your Discord application’s Client ID.

### Optional (Administrator)

If you prefer the bot to have full access (not recommended unless you trust the code and need it):

```text
https://discord.com/api/oauth2/authorize?client_id=YOUR_CLIENT_ID&permissions=8&scope=bot%20applications.commands
```

---

## 🔐 Bot Permissions (Discord)

MinerBot may require these capabilities depending on which features you enable:

- Create slash commands in your server
- Send messages in configured channels
- Post embeds
- Read message history
- Pin messages, for the `pin` message command
- Mention everyone, for `/admin announce` to ping `@everyone`, `@here` or a role that isn't mentionable

MinerBot doesn't use any privileged gateway intents, so the Message Content, Server Members and Presence switches in the Discord Developer Portal can stay off.

---

## 🚀 Running MinerBot

You can run MinerBot:

- **Locally** (Visual Studio / `dotnet run`)
- **In Docker** (if you have container files configured)

### Prerequisites

- Latest Visual Studio recommended (or a compatible .NET SDK)
- Database access (connection strings indicate MySQL-style configuration)

---

## ⚙️ Configuration

MinerBot uses:

- `appsettings.json` for application configuration (DB/Logging)
- Environment variables (or a `.env` file) for secrets (tokens/keys/passwords)

> 🔒 **Do not commit secrets**. Keep `appsettings.json` and `.env` out of Git.

### 1) `appsettings.json`

Create an `appsettings.json` file and fill in your credentials:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "SERVER={Server Host};PORT=3306;DATABASE={Website Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;CharSet=utf8mb4;",
    "BotConnection": "SERVER={Server Host};PORT=3306;DATABASE={MinerBot Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;CharSet=utf8mb4;"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

`DefaultConnection` is the website's database, which holds the armor, the blacklist and each server's channels, and the tables of the [website feed](#-website-feed). `BotConnection` is MinerBot's own database, which the web dashboard uses and where the bot records its status and history, keeps a copy of its servers ([Status and history](#-status-and-history)) and keeps the record of its posts ([Posts](#-posts)). It can be left out: the bot then runs and posts, keeping what it found and what each server is owed in memory, so a restart loses what it hasn't posted yet; it records nothing, sends no message about new armor submissions, and says so once in its log as it starts. Nothing records what it posts then, so once `BotConnection` is back, its first runs post again what came while it was left out: the armor approved meanwhile, the maps of the last 24 hours and the lobbies still open. Both end in `CharSet=utf8mb4`, the tables' own character set: without it, MySql.Data sends text in the server's default character set, which can turn an emoji into a question mark.

The dashboard writes its audit and review rows in the same transaction as the website change they record, on the website's connection, and a move back to pending from Approved reads MinerBot's record of the armor's posts in that transaction too. So the login in `DefaultConnection` also needs `INSERT` on MinerBot's `audit_log` and `armor_reviews` tables and `SELECT` on its `notification_seen`, `notification_queue`, `notification_deliveries` and `guild_delivery_status` tables, or both strings can use one login with rights on both databases.

✅ Recommended: Commit a **safe template** called `appsettings.example.json` with placeholders only.

### 2) Environment Variables (`.env`)

Provide secrets via environment variables or a `.env` file:

```env
BOTTOKEN={Discord Bot Token}
WEBHOOKCHANNEL={No longer used: keep it a valid webhook while an older image may be rolled back to}
TENORKEY={Optional Tenor API Key (optional)}
STEAMWEB_KEY={Steam Web API Key}
STEAMWEB_PUB_KEY={Steam Publisher Key (Only for MM devs with Steam developer access)}
STEAMUSER_NAME={Your Steam Username}
STEAMUSER_PASSWORD={Your Steam Password}
STEAM_QRCODE_AUTH=true
STEAMUSER_REMEMBERME=true
STEAMAPP_ID=274900
ALERT_USER_ID={Discord user ID that gets the bot's alerts (optional, the application's owner by default)}
REQUEST_SIGNING_KEY={64 hexadecimal digits in small letters, the dashboard's Dashboard__RequestSigningKey (optional)}
```

`WEBHOOKCHANNEL` isn't used any more: the bot no longer makes a webhook client at its start, which stopped the start for an unset, malformed or deleted webhook. An older image, as `./rollback.sh bot` can put back on the server, still makes it, so keep the setting a valid webhook while one may be rolled back to.

`REQUEST_SIGNING_KEY` is the key the bot checks the dashboard's requests against ([Requests to the bot](#-requests-to-the-bot)). On the server, `setup-dashboard.sh` makes it and writes it here and into the dashboard's settings. Without it, or with one in another form, the bot takes no requests: it records `requests.off` once, refuses each request with why, and runs as ever otherwise.

✅ Recommended: Commit a **safe template** called `.env.example`.

---

## ▶️ Run Locally

### Visual Studio

1. Open the solution in Visual Studio
2. Set **MinerBot 2.0** as the **Startup Project**
3. Ensure `appsettings.json` exists and env vars are set
4. Run

### CLI (optional)

```bash
dotnet restore
dotnet run
```

---

## 🐳 Run in Docker

Local settings stay out of the image, so pass them when the container starts. Add the connection strings to your `.env` file:

```env
ConnectionStrings__DefaultConnection=SERVER={Server Host};PORT=3306;DATABASE={Website Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;CharSet=utf8mb4;
ConnectionStrings__BotConnection=SERVER={Server Host};PORT=3306;DATABASE={MinerBot Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;CharSet=utf8mb4;
```

Then build from the repository root and start the container:

```bash
docker build -f "MinerBot 2.0/Dockerfile" -t minerbot .
docker run -d --name minerbot --restart unless-stopped --env-file .env -v minerbot-data:/app/data minerbot
```

`/app/data` holds the Steam login token, so keep it on a named volume.

The bot exits when it hits a failure it can't recover from, and `--restart unless-stopped` has Docker start it again. To add that to a container that already exists, run `docker update --restart unless-stopped minerbot`.

The image has a health check of its own, `dotnet "MinerBot 2.0.dll" healthcheck`: every 5 seconds while the bot is up on Discord, it rewrites a heartbeat file in `/tmp`, and the check fails once that's over a minute old. `docker inspect -f '{{.State.Health.Status}}' minerbot` says healthy, starting or unhealthy, and `docker inspect -f '{{json .State.Health}}' minerbot` shows the check's last answers. Docker only reports an unhealthy container; it doesn't restart it. A build can stamp the image with its commit, which the bot records as its version: `docker build --build-arg SOURCE_REVISION=$(git rev-parse HEAD) -f "MinerBot 2.0/Dockerfile" -t minerbot .`

---

## 📈 Status and history

With `BotConnection` set, the bot records what it does in MinerBot's own database, for the dashboard's status pages. The tables come from the dashboard's `migrate` command, which a deploy runs, after a dump, whenever the commit brings a migration MinerBot's database doesn't have yet. Only the bot writes them, with its own login, and nothing in them is secret: no token, no SteamID, no account name and no message from a library, only the bot's own wording.

- `bot_status`: one row, rewritten every 30 seconds, and at once when Discord's or Steam's state changes: the bot's version (its commit), when it started, its last heartbeat, whether it stopped cleanly, Discord's state with its latency, its number of servers and since when, the moment the bot connected or dropped, Steam's state with its last logon answer and when the saved token expires, and `posting_run_id`, the run whose posts are kept in this database ([Posts](#-posts)), which an older bot's writes leave as it was.
- `uptime_samples`: one row for each minute the bot ran, with whether Discord and Steam were up, up when any run's sample of it says so; `uptime_daily` keeps each day's totals. The minute a run starts in is sampled once that run is connected to Discord, or as down at the next minute when it never was, so a restart doesn't turn its minute down.
- `lobby_samples`: at each notification run, every 5 minutes, how many public lobbies Steam listed, of every version, with their players and slots, those of the game's current version, the full ones and the game's own Gamenight and Official lobbies: counts only, never a lobby, a host or a map, and none at all when Steam gave no answer, so an outage never reads as an empty game. A second run in the same minute keeps the first's.
- `bot_events`: what happened, such as `discord.disconnected`, `steam.logon_failed`, `job.failed`, `alert.sent` or `bot.unclean_end`.
- `job_status` and `job_runs`: each scheduled job's schedule and next run, and every run with its result (ok, failed, cut off or skipped), duration and detail.
- `guilds`, `guild_channels`, `guild_events` and `guild_daily`: the servers the bot is in or has left, with each one's name, icon, member count, when the bot joined, whether Discord reports it unavailable and whether the bot has Administrator there; each server's text and announcement channels, with whether the bot can view them, send messages and embed links there; the bot joining and leaving servers; and each server's member count once a day. The names are Discord's, the only text here that isn't the bot's own: a channel the bot can't view comes from Discord named `___hidden___`, and is kept so, with `can_view` 0.

The copy of the servers follows Discord within seconds: the bot notes each server where something changed, a channel, a role, its own roles or the server itself, and every 5 seconds writes the ones that did, so a new channel shows up in `guild_channels` within 15 seconds. It looks at every server again each time it connects to Discord with a new session, when it also marks as left the servers it was removed from while it was offline, and every 15 minutes, in case an event never came. A server in an outage is marked unavailable, never left. Without the members intent, Discord's member count is only right when Discord sends the server, so at 04:21 US Eastern each day the bot asks Discord's API for each server's count, one server at a time.

What the bot records waits in memory until it's written, up to 6 hours of uptime and lobby samples, 1,000 events and 500 job runs, so for its status and history a database that's down costs only the recording: the bot logs one warning for its status and history, and one for its servers when they can't be written either, tries again every 30 seconds to 5 minutes, and records the outage once it's back. The servers are noted rather than queued, so they're written again whole once it's back. Posts are different: the record of what each server is owed is kept in MinerBot's database ([Posts](#-posts)), so while it's down the armor, map and lobby posts wait, and each notification run fails saying "posts wait: MinerBot's database didn't answer", until the first run once it's back, which posts what came meanwhile. Both databases run on the server's one MariaDB, so MinerBot's alone failing is mostly a fault in its login's rights or its setup, which that failure names. A daily clean-up at 04:30 US Eastern keeps the audit log for 2 years with visitors' addresses cleared after 90 days, the events, the joins and leaves of servers, the job runs, the requests to the bot and the minute samples for 90 days with each day's totals kept, and dashboard sessions until 7 days after they end. The servers' rows, left ones too, and their daily member counts are kept. A second clean-up, for the posts, follows at 04:33: it works out each day's totals of the posts sent, failed or that may not have arrived, and of the items dropped, again for every day the record still holds whole, and keeps them; then it keeps the record of the posts and the queue of maps and lobbies for 90 days, the lobbies the bot hasn't seen for 7 days, and the lobby samples for 90 days. Armor's queue, where each armor was posted, is kept, so an armor posted long ago still can't be moved back to pending, and one approved again is still posted only where it never was.

---

## 📮 Posts

Every 5 minutes the notification job looks for armor approved on the website, workshop maps new or updated on Steam, and new public lobbies, and posts them in each server's channels for them, the ones `/admin set_channels` sets, unless MinerBot's owner switched that kind off for the server on MinerBot's dashboard, or the server set no channel for it. A run's detail on the Bot page says what it posted, such as `1 armor: posted in 5 of 6 servers, 1 failed`, or `nothing new to post`.

With `BotConnection`, what it found and what each server is owed is kept in MinerBot's database, so a restart, a deploy or the nightly Docker restart loses no post, and a crash doubles none:

- Each source's first good read is its baseline, which posts nothing: armor already approved, the maps in Steam's latest list and the lobbies open then, each recorded as already posted. After a gap, such as a restart that crossed a run, the job reads 20 maps rather than 5, so maps updated meanwhile are posted, the last 24 hours' worth; a read 5 minutes after the last posts what's new to it however long ago Steam dates it, such as a map made public a day after its upload.
- What's new is queued for every server owed it, in one transaction, even while posts are paused, and a server whose post failed gets it once it can, whatever the other servers got. A server switched off or without a channel when an item is found isn't owed it, and switching it on later posts only what's found from then.
- Each message is claimed in the database before it's sent, and its message ID recorded once Discord answers. When Discord doesn't answer a post within 15 seconds, or the bot stops or crashes between Discord taking a post and the bot recording it, its next run looks for the post in the channel's recent messages, where it has Read Message History, and records it as sent if it's there, or sends it again if it isn't. Where it can't read the channel, after three looks or an hour, the post is recorded as one that may not have arrived, with a `posts.unconfirmed` event, and is never sent again: a post twice in someone's server can't be taken back. An outage is different: while Discord has answered none of the bot's posts and reads since a post was tried, whether it answers with its own errors or rate limits or the bot's connection to it fails or hangs, a look that gets no answer keeps the post waiting past the hour, up to its kind's time limit (armor 7 days, maps 2 days), so the first look Discord answers finds it or sends it once. The bot can't tell one channel that alone doesn't answer from an outage until Discord answers something it asks after the post: when that post was its last before a spell with nothing to post, or the bot restarted after it, the post waits the same way, its channel read at every run, until Discord answers the bot's next post anywhere, and then counts as sent. The bot can't tell an outage of only part of Discord from one channel either: once Discord answers it anywhere, a post whose own channel still doesn't answer counts as sent at the next run, at most two such posts a run, so a server whose part of Discord is back last can miss posts. A stop claims no new message and waits up to 20 seconds for the one under way.
- Armor approved again is posted only where it never was, and so is a map shown again on Steam after it was hidden while it waited, at once, with any update made while it was hidden. A map post shows the map as it is when it goes, with any update made while it waited or before it was sent again, and that update isn't posted there again. Armor that waits is given up after 7 days, a map after 2, a lobby after 30 minutes, and those limits start again when armor is approved again or a map is shown again, however long it was withdrawn or hidden: a map shown again goes where it never went, in a read after a gap too, as long as the queue still keeps its rows, for 90 days.
- The bot checks its copy of each server first: the channel, and View Channel, Send Messages and Embed Links there. A refusal, from its copy or from Discord, waits 5 minutes before that server's kind is tried again, then 15, 1 hour, 3 hours, and 6 hours at most, and the wait ends as soon as the channel or the bot's permissions there change, the server's copy is refreshed from the dashboard, or a test message goes to that channel. A run sends at most 50 messages and claims none after 200 seconds. It waits out Discord's rate limit within a post's 15 seconds, and a rate limit that holds a post longer reads as a post Discord didn't answer. A run that meets a rate limit just as those 15 seconds run out, a refused token, a connection to Discord that won't open, two posts Discord didn't answer, or a message's claim or the record of its send, a refusal or a drop that MinerBot's database didn't take, stops posting until the next run, and its detail says why.
- After `./rollback.sh bot`, an older bot runs, which keeps none of this. The first run of the newer bot after it sees the older bot's runs in `job_runs` and takes a new baseline, keeping what still waits.

The tables, which only the bot writes: `notification_baseline`, each source's baseline and last good read; `notification_seen`, every item found; `notification_queue`, each item and server it's owed, and what became of it; `notification_deliveries`, each message sent or tried, with Discord's message ID, or why it didn't go by Discord's HTTP status and code, never Discord's words; `guild_delivery_status`, each server's last post and last failure of each kind, and the wait before its next try; and `delivery_daily`, each day's totals, kept. Beside them, `guild_settings` holds each server's switches, which MinerBot's owner saves on the dashboard and the bot reads at every run, so a change applies from the next run. A server that removes MinerBot loses its switches, its delivery status and what it was still owed.

### New-submission alerts

Every 5 minutes, 2 minutes after each notification run, the bot looks for armor sent in for a review, and at most once an hour, while new armor waits, sends the owner a direct message, to `ALERT_USER_ID` or else the owner of the Discord application: how many were sent in since the last message, how many wait in all, the oldest's date, and a link to the dashboard's list of the armor waiting for a review. It never holds an armor's name or its author's. New is armor the owner hasn't been told about, above the highest armor ID there was at the bot's first look, which takes the armor already waiting as seen, with no message; so armor moved back to pending is new only when it came after the first look and the bot never saw it waiting. A message Discord refuses, or one that never went, because Discord failed before it, as at opening the direct message, or kept answering it with a rate limit or a 502 until its 15 seconds were up, fails the job's run, which the dashboard's Overview shows, and is tried again after the hour; one Discord didn't answer may have arrived, and isn't sent again, nor is one the run stops waiting for at 30 seconds, as it may still go, even if Discord then fails before it, which the bot's events show as `alert.failed`. Without `BotConnection` it does nothing.

---

## 📨 Requests to the bot

The dashboard never talks to the bot. It leaves a request in MinerBot's own database, in `bot_commands`, and the bot, which looks every 3 seconds, takes it within seconds and writes how it ended on the same row, in its own words. The dashboard's pages send them, as the web dashboard's section below says: Bot controls, the cache clear and Announce now after an armor review, a server's test message, refresh and leave, and the blacklist's custom-link lookup, besides the message about each sign-in. The server's `deploy/request.sh` sends one too, for when the pages can't be reached ([deploy/README.md](deploy/README.md)).

Each request is signed with a key only the two apps hold, `REQUEST_SIGNING_KEY` in the bot's settings and `Dashboard__RequestSigningKey` in the dashboard's, over every field the bot acts on, so a row written or changed without the key is refused. Before the bot acts, it checks the request against one fixed list in `MinerBot.Shared`: the kind, who may ask for it, its details, that it hasn't expired, and the limit, which it counts from the table, so a restart resets nothing. It takes each request at most once, one at a time in the order they came, and only while it's up on Discord, but for a restart, which is the cure for a bot stuck off it. After each Owner-level request it acts on, it sends the owner a direct message saying who asked and what happened. The dashboard's login can add a request, naming only the columns the dashboard fills in, and read them back; only the bot's own login writes how one ended. Every request has an audit row, written with it.

| Request | What the bot does | Who may ask | At most | Waits |
|---|---|---|---|---|
| `caches.clear` | Clears its cached armor names and latest maps | Owner, Admin | 30 an hour | 2 minutes |
| `job.notifications` | Runs the notification job now | Owner, Admin | once a minute | 2 minutes |
| `job.colors` | Runs the colour job now | Owner | once a minute | 2 minutes |
| `steam.renew` | Renews the Steam token, which the daily Steam-token job also does | Owner | once every 10 minutes | 2 minutes |
| `posts.pause`, `posts.resume` | Pauses or resumes its armor, map and lobby posts. While paused, what it finds waits and is posted at the resume, unless it waited past its limit: 7 days for armor, 2 for a map and 30 minutes for a lobby; `/admin announce`, the welcome message, test messages and direct messages aren't paused | Owner | 10 an hour, together | 10 minutes |
| `status.set` | Sets the status text every server shows, 128 characters at most, with no link, mention, line break or hidden character, or back to the default, "Playing Murder Miners on my PC" | Owner | 6 an hour | 2 minutes |
| `steam.reconnect` | Disconnects from Steam and logs on again | Owner | once every 5 minutes | 2 minutes |
| `guild.test_message` | Posts a fixed, silent test embed in a text or announcement channel of one server | Owner, Admin, the server's own admins | 3 every 10 minutes, for each server | 2 minutes |
| `guild.refresh` | Rewrites its copy of one server and its channels from Discord.Net's cache | Owner, Admin, the server's own admins | once a minute, for each server | 2 minutes |
| `steam.lookup` | Looks up a custom Steam profile link's account number | Owner, Admin | 60 an hour | 2 minutes |
| `guild.leave` | Leaves a server, which deletes that server's channel settings, as a removal does | Owner | 5 an hour | 2 minutes |
| `bot.restart` | Restarts the bot: exits with code 3, and Docker starts it again. Never in the first 5 minutes after a start, nor during a notification run | Owner | once every 10 minutes | 2 minutes |
| `owner.signin_alert` | Sends the owner a direct message about a dashboard sign-in, with no address | Owner, Admin | 20 an hour | 10 minutes |

A request the bot hasn't taken by its time expires, and one it takes ends within a minute at most. The pause and the status text are kept in `bot_settings`, which only the bot writes, so a restart keeps both: while the bot hasn't read whether posts are paused, as just after a start, and for as long as MinerBot's database doesn't answer then, its posts wait. What a paused run finds is queued in MinerBot's database ([Posts](#-posts)), so it waits through a restart too, and goes out at the first run after the resume, a map that left Steam's latest list meanwhile included, unless it was hidden on Steam, waited past its limit, or its kind was switched off for that server meanwhile, which drops it there even when the kind is on again by the resume.

---

## 🌐 Website feed

The website's Lobbies and Maps pages can't ask Steam themselves, so the bot writes what it reads from Steam into three tables of the website's database, on `DefaultConnection`. The website makes the tables, in its own migration, and only reads them; the bot only writes their rows, and never makes or changes a table. [`docs/schema/wordpress-tables.sql`](docs/schema/wordpress-tables.sql) has their structure. Every time in them is UTC, in whole seconds.

- `mmh_feed_lobbies`: the public lobbies that are open right now, read every 30 seconds: every lobby `/murder-miners lobbies` would list, on every game version. Each answer from Steam replaces the whole set in one transaction: a lobby that's still open keeps its `first_seen` and takes everything else from the answer, a new one is added, and one that closed is deleted. `host_name` is the name the bot's own list shows, `Gamenight` or `Official` for a lobby of the game's own host, and `host_kind` says which it is: `player`, `gamenight` or `official`, so a player who took one of those names is still a `player`. Where the list would say Host Unknown, Map Unknown or Modes Unknown, `host_name`, `map_name` and `modes` are empty, and `modes` is never a guess. `version_label` is the branch's name, such as `Update 41`, when the version is a branch's own, and empty otherwise; `is_current` is 1 on the version the bot counts as the game's current one.
- `mmh_feed_maps`: Steam's latest public maps, new or updated, about 30 of them every 5 minutes, one row a map. A map that's read again keeps its `first_seen` and takes everything else from Steam. A map that was hidden or removed on Steam since the last good read is deleted: a row that read held, that this one doesn't, and whose `time_updated` is no earlier than this read reaches down Steam's list, which is the newest update among its last three maps. Every other row stays, as the page's history. Steam's list is ordered by last update, but not exactly by `time_updated`: now and then it ranks a map far above its place, so the rule never goes by a read's oldest map, and never touches a row the last read didn't hold. The price is that a hidden map among a read's last two stays. `description` is plain text: Steam's BBCode is taken out as for the bot's map embeds, nothing is escaped, and it's cut at 2,000 characters; a map without one has `NULL`. `tags` are separated by commas, without spaces around them. `time_created` and `time_updated` are Steam's own, and `first_seen` and `last_seen` the bot's.
- `mmh_feed_state`: one row for each feed, `lobbies` and `maps`. `checked_at` is when the bot last tried, `ok_at` when it last got an answer from Steam and wrote it, and `item_count` how many rows that wrote. A read where Steam gave no answer changes no lobby or map, only `checked_at`, so a page can tell a feed that's behind from an empty one.

The feed is no Discord post, so pausing the bot's posts ([Requests to the bot](#-requests-to-the-bot)) doesn't pause it, and it shares nothing with the notification job: it reads Steam itself and keeps no list of what it has seen. It isn't a scheduled job either, so it has no rows in `job_runs`. It reads the lobby list ten times as often as the notification job, so the line a failed read of it writes to the log comes once every 5 minutes for the feed's reads, not at each one.

Nothing else the bot does depends on it. While the website's database is down, the tables aren't made yet, or the bot's login lacks a right on them, the bot runs as ever and each feed logs one warning with MariaDB's error number, 1146 for a missing table and 1142 for a missing right, then tries again after 30 seconds, then less often, up to every 5 minutes, and logs one line when its writes go through again. So the bot can be deployed before the website has made the tables, and starts writing within 5 minutes of them being there, without a restart.

The bot's website login, the one in `DefaultConnection`, needs `SELECT`, `INSERT`, `UPDATE` and `DELETE` on the three tables, and nothing else for the feed. Where it holds rights table by table, they're given once, as root, after the website has made the tables, since MariaDB refuses a right on a table that doesn't exist yet. [`deploy/README.md`](deploy/README.md) has the statements.

---

## 🖥️ Web dashboard

`MinerBot.Dashboard` is a web app at https://bot.murderminershub.com for the people who run MinerBot. You sign in with Discord, and only the accounts on its lists, with Discord two-factor turned on, get in. The owner and the admins review armor submissions there, under Armor: each one's text with its hidden characters shown, its screenshot when it's an upload on the website, the approval rules, and how the bot will post it. They can approve, reject, take down or move it back to pending, and each change is kept in its history and the audit log. Under Blacklist they keep the list of players to ban, which nothing enforces yet, until the game's API can: each entry is an account, entered as a SteamID in any form, or a custom profile link, which the bot looks up first, with a name, a reason, an end date in UTC and notes. The Audit log lists every change made on the dashboard, every sign-in and sign-out, and the changes and sign-ins it refused, newest first, filtered by person, action, server or days; only the owner sees each row's visitor address. A form sent back to correct a field, a request to the bot held back by the bot's own limit, a form that had expired or was incomplete, an action on armor that had since been removed, a cancelled or failed sign-in, and a request turned away before it reached its page, such as one from another site or one over the rate limit, aren't recorded, but the request that ends the sessions of someone taken off the lists is.

The pages about the bot itself read what it records ([Status and history](#-status-and-history)), so they still work while it's down. The Overview says whether the bot is online, late, offline or stopped and when it last checked in, Discord's and Steam's state, whether the website's database answers the dashboard, the last notification run, how many armor wait for a review and the latest changes, with a banner for anything that needs someone, each saying what to do and linking where to do it, among them servers whose posts keep failing, 3 tries in a row or more of a kind that's on, and a bot that's older than the dashboard, as after `./rollback.sh bot`, which ignores the switches and records no posts. Bot shows the version the bot runs, its starts and the runs that ended without a clean stop, its uptime on Discord and Steam over 24 hours, 7, 30 and 90 days with a strip of the last 90 days, each job's schedule in Eastern time, last run, next run and last 50 runs, and its events, 50 to a page, leaving out the disconnects Discord asks for when it has the bot reconnect, unless you ask for them, though the connection after each one still shows. Servers lists every server the bot is in or has left, with each one's four notification channels marked OK, can't post, not set up, off, switched off, another server's channel or can't check, which is what a setting shows when its channel isn't a text or announcement channel, the only ones the bot's copy keeps: a voice channel's chat, which the bot's posts reach, or a forum or a deleted channel, which they don't. Under each mark is the bot's last post of that kind there, a link to it in Discord, or since when its posts fail, and Has problems counts a server whose posts of a kind that's on keep failing. Each server's page shows the same with the reason a post failed in plain words, Discord's numbers after them, its tries in a row and the next try, its switches, its last 25 posts with a link to each one that went, its channels in Discord's order with what the bot can do in each, its joins and leaves, its members over 90 days and the changes made to it on the dashboard. The owner turns a server's armor, map or lobby posts off or on there, while MinerBot is in the server, at most 30 times an hour; the bot reads the switches at its next notification run, and an admin sees them without the form. An armor's review page says where the bot posted it, from the bot's record, with a link to each post, and offers Back to pending from Approved only while the record shows no post of it, which the move checks again as it's saved. The Overview's and Bot's status box refreshes itself every 30 seconds while the page is open, with the dashboard's one script; times are UTC.

The dashboard also asks the bot to act, through signed requests ([Requests to the bot](#-requests-to-the-bot)). Bot controls, at `/admin/bot/controls`, has each of the owner's controls, with who used it last, when and the bot's answer, and whether one more would be sent now: pausing and resuming posts, the status text, the notification and colour jobs, clearing the caches, reconnecting Steam, renewing its token and restarting the bot. An admin sees their two there, the cache clear and the notification job. An approval, a take-down or a move back from Approved asks the bot to clear its armor-name cache, and newly approved armor offers Announce now until the bot has found it, which its next notification run does, restarts included: it posts the armor then in every server with armor posts on. A server's page sends a test message to one of the channels that server chose for MinerBot's posts that can take an embed, or has the bot rewrite its copy of the server, and the owner can make the bot leave a server on a page of its own. Restarting, pausing posts, the Steam controls, a status text, leaving a server and ending sessions need a sign-in from the last 15 minutes, which the page offers when it's older; restarting, pausing posts and the Steam controls also need their word typed, and a leave the server's name or ID. Each person has an hourly share of what the bot takes, and the bot's own limit is checked before anything is sent. A request's box follows it until the bot has answered, and its own page, which the audit log's row links, says what the bot did for the 90 days the requests are kept.

While requests to the bot are on, every sign-in asks the bot to send the owner a direct message about it, the owner's own included, in the same transaction as the session, so a sign-in whose request can't be written doesn't happen; with them off, sign-ins go ahead without one. A sign-in again that fails leaves the session it would have replaced.

Sessions and security, at `/admin/sessions`, the owner's alone, lists who is signed in and from where, ends one session or every other one, and checks the dashboard itself: its database login's rights and limits, the sign-in keys' folder, MinerBot's database structure, Cloudflare Access, requests to the bot, the owner's direct messages and the sign-in messages.

Sign-in uses a Discord application of its own, MinerBot Dashboard, with the `identify` scope only, and the dashboard revokes the Discord token as soon as it has read who signed in. Sessions are kept in MinerBot's own database and end after 30 minutes unused, or 12 hours at most. The status box's refresh doesn't count as use, so a page left open still ends its session.

### Settings

The dashboard reads environment variables and checks them all as it starts. A missing or wrong one stops it with a plain message that names the setting. It reads them only then, so after changing one, such as the lists, create the container again: `docker restart` keeps the old values. `dashboard.env.example` has the same list with comments. The values below are placeholders:

| Variable | What it is |
|---|---|
| `Dashboard__PublicOrigin` | `https://bot.murderminershub.com`, the address people open. Discord's sign-in comes back to `/signin-discord` there. |
| `Dashboard__ProxyKey` | `{Proxy Key}`: 64 random hex characters that nginx adds to every request as `X-MinerBot-Proxy-Key`. Without it, the dashboard answers 404. Only letters, digits, `-` and `_` are allowed, because nginx's configuration reads other characters as variables or escapes. |
| `Dashboard__RequestSigningKey` | Optional: `{Request Key}`, the key the dashboard signs its requests to the bot with ([Requests to the bot](#-requests-to-the-bot)), the same as the bot's `REQUEST_SIGNING_KEY`: 64 hexadecimal digits in small letters, which `setup-dashboard.sh` makes. Empty turns requests to the bot off; any other form stops the dashboard at its start. |
| `Dashboard__Owners` | `{Owner's Discord User ID}` |
| `Dashboard__Admins` | `{Admins' Discord User IDs, separated by commas}` (optional) |
| `Dashboard__KeysFolder` | `{Keys Folder}`, where the sign-in keys are kept so a restart signs nobody out. The image sets `/app/keys`. |
| `Dashboard__ListenAddress` | `127.0.0.1` unless set. Only a loopback address is allowed. |
| `Dashboard__Port` | `5080` unless set |
| `Dashboard__UnixSocket` | Optional: a Unix socket to listen on instead of the address and port, as a full path of at most 100 bytes, such as `/run/minerbot-dashboard/dashboard.sock`. On the server `compose.yml` sets it, in a folder only the dashboard can write to and only processes in the group `nobody` can open, nginx's workers and cPanel's Apache among them, so no other account on the server can take the dashboard's place while it restarts. A socket a killed dashboard left there is replaced as it starts; anything else at that path stops it. |
| `Discord__ClientId` | `{MinerBot Dashboard Client ID}` |
| `Discord__ClientSecret` | `{MinerBot Dashboard Client Secret}` |
| `Discord__AuthorizationEndpoint`, `Discord__TokenEndpoint`, `Discord__RevocationEndpoint`, `Discord__UserInformationEndpoint` | Optional: Discord's own unless set, and https only |
| `CloudflareAccess__TeamDomain` | Optional: `{Team Name}.cloudflareaccess.com`, the Cloudflare Access team's host name and nothing else. With it and the AUD tag, `/admin` also needs the signed token Access adds to each request it let through, checked against the team's keys. |
| `CloudflareAccess__Audience` | Optional: the Access application's AUD tag, 64 characters of `0-9` and `a-f`. Set both or neither: with both empty the check is off. |
| `ConnectionStrings__DefaultConnection` | The website's database, in the format above |
| `ConnectionStrings__BotConnection` | MinerBot's own database, in the format above |

> ⚠️ Never set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `AllowedHosts` or `Kestrel__Endpoints__...` for the dashboard. It trusts forwarded headers only from nginx on the same server, answers only its own host name and `localhost`, and listens only where `Dashboard__UnixSocket`, or else `Dashboard__ListenAddress` and `Dashboard__Port`, say. It sets all three in code, so none of them can be loosened from outside.

### Run it locally

With made-up settings, and without a database for anything but signed-in pages:

```bash
export Dashboard__PublicOrigin=http://localhost:5080
export Dashboard__ProxyKey=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
export Dashboard__Owners=1000000000000000001
export Dashboard__KeysFolder="$HOME/minerbot-dashboard-keys"
export Discord__ClientId=1000000000000000009
export Discord__ClientSecret=made-up-secret
export ConnectionStrings__DefaultConnection="SERVER=127.0.0.1;PORT=3306;DATABASE={Website Database Name};UID={User ID};PASSWORD={Password};CharSet=utf8mb4;"
export ConnectionStrings__BotConnection="SERVER=127.0.0.1;PORT=3306;DATABASE={MinerBot Database Name};UID={User ID};PASSWORD={Password};CharSet=utf8mb4;"
dotnet run --project MinerBot.Dashboard
```

Then, from another terminal:

```bash
curl -H 'Host: localhost' http://127.0.0.1:5080/health/live
curl -H 'Host: localhost' -H 'X-Forwarded-Proto: https' -H 'X-MinerBot-Proxy-Key: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef' http://127.0.0.1:5080/
curl -i -H 'Host: localhost' http://127.0.0.1:5080/
```

The first answers `Healthy`, the second shows the home page, and the third, without the key, gets 404. Plain http is enough for those, but not for signing in: the session and form cookies only travel over https, and every signed-in page has a form. To sign in for real on your own machine:

- put a proxy in front that serves https and adds `X-MinerBot-Proxy-Key` and `X-Forwarded-Proto: https` to each request;
- set `Dashboard__PublicOrigin` to the proxy's https address. The dashboard sends HSTS, so give it a name of its own, such as `https://dashboard.localhost:8443`, rather than plain `localhost`, or your browser will insist on https for every site on localhost for a year;
- register `<that address>/signin-discord` as the redirect of a Discord application of your own.

The tests cover sign-in with a fake Discord instead.

### Commands

The same program runs the deploy's jobs, and request.sh's, without starting the web app:

- `dotnet MinerBot.Dashboard.dll migrate` brings MinerBot's own database up to date, using only `ConnectionStrings__BotConnection`, and exits 0 or 1.
- `dotnet MinerBot.Dashboard.dll healthcheck` asks the running dashboard's `/health/live` where it listens, on its socket when `Dashboard__UnixSocket` is set, and exits 0 or 1. The image uses it as its health check, because it has no curl.
- `dotnet MinerBot.Dashboard.dll check-settings` checks the settings the way a start would, then exits: 0 with "The dashboard's settings are fine.", or 1 with one line for each problem, naming the setting but never its value. Nothing listens and nothing connects. The deploy runs it on the new image after the build and any migrations, before the rights and the swap. Setup runs it on the `dashboard.env` it's about to write, after it undoes a cut-off nginx change if one is left, and before it changes the database login, the rights, nginx's files or `dashboard.env`.
- `dotnet MinerBot.Dashboard.dll request <kind> [--guild <server ID>] [--channel <channel ID>]` sends the bot one request ([Requests to the bot](#-requests-to-the-bot)), through the same code the dashboard's pages use, as the first of `Dashboard__Owners`, named "request.sh on the server", then waits for the bot's answer and prints it; after a restart, it waits for the bot to be back on Discord. It reads its settings, `ConnectionStrings__BotConnection`, `Dashboard__RequestSigningKey` and `Dashboard__Owners`, and the request's text, `MINERBOT_REQUEST_TEXT`, from the environment only, never its command line, and prints no key or connection string. Exit code 0 done, 1 refused, failed, expired, unknown or no answer, 2 a bad argument or setting. `request --list` lists the kinds. The server's `deploy/request.sh` runs it.

### In Docker

Build from the repository root, and keep the keys on a named volume:

```bash
docker build -f MinerBot.Dashboard/Dockerfile -t minerbot-dashboard .
docker run -d --name minerbot-dashboard --network host --read-only --tmpfs /tmp --env-file dashboard.env -v minerbot-dashboard-keys:/app/keys minerbot-dashboard
```

The image runs as its non-root user, listens on nothing but `127.0.0.1:5080`, or only the socket `Dashboard__UnixSocket` names, and writes only to `/app/keys` and `/tmp`, and to the socket's folder when it's set. Keep `--tmpfs /tmp` with `--read-only`: each new sign-in key is written to `/tmp` first, and the dashboard won't start without somewhere to write it. Its log warns "No XML encryptor configured" when it makes its first key, which is expected on Linux: keep the volume readable by the container's user only.

### Hosting

On the server, the bot and the dashboard run with Docker Compose, behind nginx and Cloudflare, and scripts build, deploy, roll back and back them up. There the dashboard listens only on a Unix socket, which nginx passes visitors to. [`deploy/README.md`](deploy/README.md) has the files and where each one goes, every script's use, the first setup in GitHub, Discord and Cloudflare, how to take the dashboard away, the rules for settings and database rights, backups and the restore, and the tests.

---

## 🔑 Steam Sign-In

MinerBot signs in to Steam with a saved token in `data/Auth/SteamToken.txt` and renews it every day.

If Steam rejects the token, the bot stops using it and sends a direct message to the user whose ID is in `ALERT_USER_ID`. Without that setting it goes to the owner of the Discord application, which for an application that belongs to a team is the team's owner. Lobbies stay offline until someone signs in again:

- **`STEAM_QRCODE_AUTH=true`:** scan the newest QR code in the bot's log with the Steam Mobile App, restarting the bot first if the log isn't showing one. In Docker, that's `docker restart minerbot`, then `docker logs -f --tail 40 minerbot`. A new code appears about every 20 seconds for 10 minutes.
- **`STEAM_QRCODE_AUTH=false`:** the bot tries `STEAMUSER_PASSWORD` up to 3 times, and may ask for a Steam Guard code on its console. An unanswered prompt ends the tries. After that it shows QR codes in its log the same way.

If Steam gives an answer that doesn't say what's wrong with the token, the bot keeps trying it for a day before asking for a sign-in. A restart tries it again and starts that day over, so if a restart shows no QR code, rename the token file and restart again. A rejected token otherwise stays in the file until a new sign-in replaces it.

To sign in with a different account, rename the token file, restart and scan with that account. In password mode, change `STEAMUSER_NAME` and `STEAMUSER_PASSWORD` too, which in Docker means creating the container again.

The direct message arrives only if that user shares a server with the bot and accepts direct messages from it. To check, rename the token file to `SteamToken.txt.bak` and restart, then rename it back and restart again. In password mode this really signs in, so it can send a Steam Guard code.

---

## 🧩 Troubleshooting

- **Slash commands not showing up?**
  - Ensure the bot invite included the `applications.commands` scope.
  - Reuse the invite link to update scopes/permissions (no need to kick the bot).
  - Check that members have the **Use Application Commands** permission in that channel.

- **Database connection issues**
  - Verify host/port/credentials in `DefaultConnection`.
  - For the bot's status and history and its copy of the servers, the same in `BotConnection`: the bot's log has one warning for each when its writes start failing, with the error's number. Posts wait while MinerBot's database doesn't answer, and each notification run's detail on the Bot page says so, with the error's number.
  - For the website's Lobbies and Maps pages only: the bot's log has one warning for each feed when its writes start failing, with the error's number and what it means ([Website feed](#-website-feed)).

- **Steam features not working**
  - Verify `STEAMWEB_KEY` (and publisher keys if applicable).
  - Check for a direct message from the bot, and see [Steam Sign-In](#-steam-sign-in).

---

## 🤝 Contributing

Contributions are welcome!

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Commit your changes: `git commit -m "Add my feature"`
4. Push: `git push origin feature/my-feature`
5. Open a Pull Request

### Projects

- `MinerBot 2.0`: the bot.
- `DataAccessLibrary`: all of the SQL, for the website's tables and for MinerBot's own database, with the migrations that create MinerBot's tables.
- `SteamWebAPIAccess`: Steam sign-in, lobbies and the Steam Web API.
- `MinerBot.Shared`: rules and formats that the bot and the dashboard share, such as the armor approval rules, SteamID formats, Discord's limits, and the list of requests to the bot with its signing code. It references nothing outside .NET.
- `MinerBot.Dashboard`: the web dashboard. It references `DataAccessLibrary` and `MinerBot.Shared`, and nothing of the bot's or of Steam's.
- `MinerBot.Tests`, `SteamWebAPIAccess.Tests`, `DataAccessLibrary.Tests`, `MinerBot.Shared.Tests` and `MinerBot.Dashboard.Tests`: the tests.
- `docs/schema/wordpress-tables.sql`: the structure of the website tables MinerBot uses: the armor, the blacklist, each server's channels, and the three tables of the website feed.

### Tests

```bash
dotnet test --solution "MinerBot 2.0.sln"
```

The database tests start MariaDB 11.4 in Docker, so the first run downloads it. Without Docker they're skipped, with the reason. Set `MINERBOT_DB_TESTS=required`, as CI does, to make that a failure.

The dashboard's tests start their own MariaDB the same way, and run the whole app in process with a fake Discord and a fake Cloudflare Access, so nothing they do reaches discord.com or Cloudflare. They read the bot's lists of its event kinds, and its jobs, as text, without referencing the bot, so a kind or a job the bot gains without a label on the dashboard fails them. `DataAccessLibrary.Tests` also applies `deploy/dashboard-rights.sql` to a login of its own, and checks that the dashboard can do everything it does today and nothing else.

---

## 📄 License

**License (noncommercial):** **PolyForm Noncommercial 1.0.0**

- ✅ Allows use, modification, and distribution for **noncommercial purposes**.
- ❌ Does **not** allow commercial use.

---

## 👤 Author / Credits

Created by **24k Connect LLC (24kConnect)** [https://24kconnect.com]

For more information: https://murderminershub.com/discord-bot/

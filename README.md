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
- Steam profile utilities (SteamID conversions)
- Admin tools: announcements + channel setup

> More features coming soon!

---

## 🧾 Commands

### Admin *(Admin Only)*

- `/admin announce [message]` — Posts an announcement of up to 2,000 characters in the server's announcements channel.
- `/admin set_channels (optional) [armor-channel] (optional) [map-channel] (optional) [lobby-channel] (optional) [announcements-channel]` — Sets where the bot posts each kind of notification. Any kind you leave out is turned off, so list every channel you want to keep.
- `/admin show_channels` — Shows each kind of notification with its channel, or `off`.

The bot needs View Channel, Send Messages and Embed Links in each channel you choose, and `/admin set_channels` saves nothing until it has them.

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

`DefaultConnection` is the website's database, which holds the armor, the blacklist and each server's channels. `BotConnection` is MinerBot's own database, which the web dashboard uses and where the bot records its status and history and keeps a copy of its servers ([Status and history](#-status-and-history)). It can be left out: the bot then runs and posts as before, records nothing, and says so once in its log as it starts. Both end in `CharSet=utf8mb4`, the tables' own character set: without it, MySql.Data sends text in the server's default character set, which can turn an emoji into a question mark.

The dashboard writes its audit and review rows in the same transaction as the website change they record, on the website's connection. So the login in `DefaultConnection` also needs `INSERT` on MinerBot's `audit_log` and `armor_reviews` tables, or both strings can use one login with rights on both databases.

✅ Recommended: Commit a **safe template** called `appsettings.example.json` with placeholders only.

### 2) Environment Variables (`.env`)

Provide secrets via environment variables or a `.env` file:

```env
BOTTOKEN={Discord Bot Token}
WEBHOOKCHANNEL={Optional Webhook Channel (optional)}
TENORKEY={Optional Tenor API Key (optional)}
STEAMWEB_KEY={Steam Web API Key}
STEAMWEB_PUB_KEY={Steam Publisher Key (Only for MM devs with Steam developer access)}
STEAMUSER_NAME={Your Steam Username}
STEAMUSER_PASSWORD={Your Steam Password}
STEAM_QRCODE_AUTH=true
STEAMUSER_REMEMBERME=true
STEAMAPP_ID=274900
ALERT_USER_ID={Discord user ID that gets the bot's alerts (optional, the application's owner by default)}
```

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

- `bot_status`: one row, rewritten every 30 seconds, and at once when Discord's or Steam's state changes: the bot's version (its commit), when it started, its last heartbeat, whether it stopped cleanly, Discord's state with its latency and number of servers, and Steam's state with its last logon answer and when the saved token expires.
- `uptime_samples`: one row for each minute the bot ran, with whether Discord and Steam were up; `uptime_daily` keeps each day's totals.
- `bot_events`: what happened, such as `discord.disconnected`, `steam.logon_failed`, `job.failed`, `alert.sent` or `bot.unclean_end`.
- `job_status` and `job_runs`: each scheduled job's schedule and next run, and every run with its result (ok, failed, cut off or skipped), duration and detail.
- `guilds`, `guild_channels`, `guild_events` and `guild_daily`: the servers the bot is in or has left, with each one's name, icon, member count, when the bot joined, whether Discord reports it unavailable and whether the bot has Administrator there; each server's text and announcement channels, with whether the bot can view them, send messages and embed links there; the bot joining and leaving servers; and each server's member count once a day. The names are Discord's, the only text here that isn't the bot's own: a channel the bot can't view comes from Discord named `___hidden___`, and is kept so, with `can_view` 0.

The copy of the servers follows Discord within seconds: the bot notes each server where something changed, a channel, a role, its own roles or the server itself, and every 5 seconds writes the ones that did, so a new channel shows up in `guild_channels` within 15 seconds. It looks at every server again each time it connects to Discord with a new session, when it also marks as left the servers it was removed from while it was offline, and every 15 minutes, in case an event never came. A server in an outage is marked unavailable, never left. Without the members intent, Discord's member count is only right when Discord sends the server, so at 04:21 US Eastern each day the bot asks Discord's API for each server's count, one server at a time.

What the bot records waits in memory until it's written, up to 6 hours of uptime samples, 1,000 events and 500 job runs, so a database that's down costs only the recording: the bot logs one warning for its status and history, and one for its servers when they can't be written either, tries again every 30 seconds to 5 minutes, and records the outage once it's back. The servers are noted rather than queued, so they're written again whole once it's back. A daily clean-up at 04:30 US Eastern keeps the audit log for 2 years with visitors' addresses cleared after 90 days, the events, the joins and leaves of servers, the job runs and the minute samples for 90 days with each day's totals kept, and dashboard sessions until 7 days after they end. The servers' rows, left ones too, and their daily member counts are kept.

---

## 🖥️ Web dashboard

`MinerBot.Dashboard` is a web app at https://bot.murderminershub.com for the people who run MinerBot. You sign in with Discord, and only the accounts on its lists, with Discord two-factor turned on, get in. The owner and the admins review armor submissions there, under Armor: each one's text with its hidden characters shown, its screenshot when it's an upload on the website, the approval rules, and how the bot will post it. They can approve, reject, take down or move it back to pending, and each change is kept in its history and the audit log. Under Blacklist they keep the list of players to ban, which nothing enforces yet, until the game's API can: each entry is an account, entered as a SteamID in any form, with a name, a reason, an end date in UTC and notes. The Audit log lists every change made on the dashboard, every sign-in and sign-out, and the changes and sign-ins it refused, newest first, filtered by person, action, server or days; only the owner sees each row's visitor address. A form sent back to correct a field, a form that had expired or was incomplete, an action on armor that had since been removed, a cancelled or failed sign-in, and a request turned away before it reached its page, such as one from another site or one over the rate limit, aren't recorded, but the request that ends the sessions of someone taken off the lists is.

The pages about the bot itself read what it records ([Status and history](#-status-and-history)), so they still work while it's down, and they change nothing. The Overview says whether the bot is online, late, offline or stopped and when it last checked in, Discord's and Steam's state, whether the website's database answers the dashboard, the last notification run, how many armor wait for a review and the latest changes, with a banner for anything that needs someone, each saying what to do and linking where to do it. Bot shows the version the bot runs, its starts and the runs that ended without a clean stop, its uptime on Discord and Steam over 24 hours, 7, 30 and 90 days with a strip of the last 90 days, each job's schedule in Eastern time, last run, next run and last 50 runs, and its events, 50 to a page, leaving out the disconnects Discord asks for when it has the bot reconnect, unless you ask for them, though the connection after each one still shows. Servers lists every server the bot is in or has left, with each one's four notification channels marked OK, can't post, not set up, off, another server's channel or can't check, which is what a setting shows when its channel isn't a text or announcement channel, the only ones the bot's copy keeps: a voice channel's chat, which the bot's posts reach, or a forum or a deleted channel, which they don't; each server's page shows its channels in Discord's order with what the bot can do in each, its joins and leaves, its members over 90 days and the changes made to it on the dashboard. The Overview's and Bot's status box refreshes itself every 30 seconds while the page is open, with the dashboard's one script; times are UTC.

Sign-in uses a Discord application of its own, MinerBot Dashboard, with the `identify` scope only, and the dashboard revokes the Discord token as soon as it has read who signed in. Sessions are kept in MinerBot's own database and end after 30 minutes unused, or 12 hours at most. The status box's refresh doesn't count as use, so a page left open still ends its session.

### Settings

The dashboard reads environment variables and checks them all as it starts. A missing or wrong one stops it with a plain message that names the setting. It reads them only then, so after changing one, such as the lists, create the container again: `docker restart` keeps the old values. `dashboard.env.example` has the same list with comments. The values below are placeholders:

| Variable | What it is |
|---|---|
| `Dashboard__PublicOrigin` | `https://bot.murderminershub.com`, the address people open. Discord's sign-in comes back to `/signin-discord` there. |
| `Dashboard__ProxyKey` | `{Proxy Key}`: 64 random hex characters that nginx adds to every request as `X-MinerBot-Proxy-Key`. Without it, the dashboard answers 404. Only letters, digits, `-` and `_` are allowed, because nginx's configuration reads other characters as variables or escapes. |
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

The same program runs the deploy's three jobs, without starting the web app:

- `dotnet MinerBot.Dashboard.dll migrate` brings MinerBot's own database up to date, using only `ConnectionStrings__BotConnection`, and exits 0 or 1.
- `dotnet MinerBot.Dashboard.dll healthcheck` asks the running dashboard's `/health/live` where it listens, on its socket when `Dashboard__UnixSocket` is set, and exits 0 or 1. The image uses it as its health check, because it has no curl.
- `dotnet MinerBot.Dashboard.dll check-settings` checks the settings the way a start would, then exits: 0 with "The dashboard's settings are fine.", or 1 with one line for each problem, naming the setting but never its value. Nothing listens and nothing connects. The deploy runs it on the new image after the build and any migrations, before the rights and the swap. Setup runs it on the `dashboard.env` it's about to write, after it undoes a cut-off nginx change if one is left, and before it changes the database login, the rights, nginx's files or `dashboard.env`.

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
  - For the bot's status and history and its copy of the servers only, the same in `BotConnection`: the bot's log has one warning for each when its writes start failing, with the error's number.

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
- `MinerBot.Shared`: rules and formats that the bot and the dashboard share, such as the armor approval rules, SteamID formats and Discord's limits. It references nothing outside .NET.
- `MinerBot.Dashboard`: the web dashboard. It references `DataAccessLibrary` and `MinerBot.Shared`, and nothing of the bot's or of Steam's.
- `MinerBot.Tests`, `SteamWebAPIAccess.Tests`, `DataAccessLibrary.Tests`, `MinerBot.Shared.Tests` and `MinerBot.Dashboard.Tests`: the tests.
- `docs/schema/wordpress-tables.sql`: the structure of the three website tables MinerBot uses.

### Tests

```bash
dotnet test --solution "MinerBot 2.0.sln"
```

The database tests start MariaDB 11.4 in Docker, so the first run downloads it. Without Docker they're skipped, with the reason. Set `MINERBOT_DB_TESTS=required`, as CI does, to make that a failure.

The dashboard's tests start their own MariaDB the same way, and run the whole app in process with a fake Discord and a fake Cloudflare Access, so nothing they do reaches discord.com or Cloudflare. They read the bot's lists of its event kinds as text, without referencing the bot, so a kind the bot gains without a label on the dashboard fails them. `DataAccessLibrary.Tests` also applies `deploy/dashboard-rights.sql` to a login of its own, and checks that the dashboard can do everything it does today and nothing else.

---

## 📄 License

**License (noncommercial):** **PolyForm Noncommercial 1.0.0**

- ✅ Allows use, modification, and distribution for **noncommercial purposes**.
- ❌ Does **not** allow commercial use.

---

## 👤 Author / Credits

Created by **24k Connect LLC (24kConnect)** [https://24kconnect.com]

For more information: https://murderminershub.com/discord-bot/

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

- `/maps latest` — Returns **5** detailed results for the newest maps on the Murder Miners Workshop.
- `/maps lookup [map-url]` — Returns detailed info for the provided Workshop map link.

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
    "DefaultConnection": "SERVER={Server Host};PORT=3306;DATABASE={Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  }
}
```

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

Local settings stay out of the image, so pass them when the container starts. Add the connection string to your `.env` file:

```env
ConnectionStrings__DefaultConnection=SERVER={Server Host};PORT=3306;DATABASE={Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;
```

Then build from the repository root and start the container:

```bash
docker build -f "MinerBot 2.0/Dockerfile" -t minerbot .
docker run -d --name minerbot --restart unless-stopped --env-file .env -v minerbot-data:/app/data minerbot
```

`/app/data` holds the Steam login token, so keep it on a named volume.

The bot exits when it hits a failure it can't recover from, and `--restart unless-stopped` has Docker start it again. To add that to a container that already exists, run `docker update --restart unless-stopped minerbot`.

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

---

## 📄 License

**License (noncommercial):** **PolyForm Noncommercial 1.0.0**

- ✅ Allows use, modification, and distribution for **noncommercial purposes**.
- ❌ Does **not** allow commercial use.

---

## 👤 Author / Credits

Created by **24k Connect LLC (24kConnect)** [https://24kconnect.com]

For more information: https://murderminershub.com/discord-bot/

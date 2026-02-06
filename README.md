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

- `/admin announce [message]` — Creates an announcement.
- `/admin set_channels [armor-channel] [map-channel] [lobby-channel] [announcements-channel]` — Sets channels used for bot notifications.

### Armor

- `/armor list (optional) [helmet-type]` — Lists all armor styles (optionally filtered by helmet type).
- `/armor lookup [armor-name]` — Finds an armor style by name (use `/armor list` to browse).

### Maps

- `/maps latest` — Returns **5** detailed results for the newest maps on the Murder Miners Workshop.
- `/maps lookup [map-url]` — Returns detailed info for the provided Workshop map link.

### Steam / Utility

- `/more steam_id [profile-url]` — Returns all SteamID formats for the supplied Steam profile URL.

### Info

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

**Permissions integer (recommended):** `84992`

Invite URL template:

```text
https://discord.com/api/oauth2/authorize?client_id=YOUR_CLIENT_ID&permissions=84992&scope=bot%20applications.commands
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
- Read and send messages in configured channels
- Post embeds
- Read message history
- *(Optional)* Administrator for admin messaging / channel automation

---

## 🚀 Running MinerBot

You can run MinerBot:

- **Locally** (Visual Studio / `dotnet run`)
- **In Docker** (if you have container files configured)

### Prerequisites

- Latest Visual Studio recommended (or a compatible .NET SDK)
- Database access (connection strings indicate MySQL-style configuration)
- Optional Redis (if used in your deployment)

---

## ⚙️ Configuration

MinerBot uses:

- `appsettings.json` for application configuration (DB/Redis/Logging)
- Environment variables (or a `.env` file) for secrets (tokens/keys/passwords)

> 🔒 **Do not commit secrets**. Keep `appsettings.json` and `.env` out of Git.

### 1) `appsettings.json`

Create an `appsettings.json` file and fill in your credentials:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "SERVER={Server Host};PORT=3306;DATABASE={Database Name};UID={User ID};PASSWORD={Password};SSLMODE=Preferred;Connection Timeout=30;",
    "HangfireConnection": "SERVER={Server Host};DATABASE={Database Name};UID={User ID};PWD={Password};",
    "Redis": "127.18.0.1:6379",
    "RedisRemote": "{Redis Host Address}:5002"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Hangfire": "Information",
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

## 🧩 Troubleshooting

- **Slash commands not showing up?**
  - Ensure the bot invite included the `applications.commands` scope.
  - Reuse the invite link to update scopes/permissions (no need to kick the bot).

- **Database connection issues**
  - Verify host/port/credentials in `DefaultConnection`.

- **Steam features not working**
  - Verify `STEAMWEB_KEY` (and publisher keys if applicable).

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

**Recommended license (noncommercial):** **PolyForm Noncommercial 1.0.0**

- ✅ Allows use, modification, and distribution for **noncommercial purposes**.
- ❌ Does **not** allow commercial use.

If you want to allow commercial use later, consider offering a separate commercial license.

---

## 👤 Author / Credits

Created by **24k Connect LLC (24kConnect)**

For more information: https://murderminershub.com/discord-bot/

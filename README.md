# MinerBot
A Murder Miners Discord Bot

Miner Bot 2.0 Slash Commands
Streamline your Murder Miners Experience.... more coming soon!

/admin announce [message] – Creates an announcement. Admin Only.

/admin set_channels [armor-channel] [map-channel] [lobby-channel] [announcements-channel] – Sets Channels for Notifications. Admin.

/armor list (Optional) [helmet-type] – Gets a list of all armor styles. Optionally, filter by helmet type.

/armor lookup [armor-name] – Find the armor style you are looking for. Use /armor list to find all armor styles.

/maps latest – Returns the 5 detailed results of the most Newst Maps on the Murder Miners Workshop.

/maps lookup [map-url] – Returns a detailed result of the Murder Miners Workshop Map Link you provided.

/more info – More Info and Credits about Miner Bot

/more steam_id [profile-url] – Returns all Steam ID formats for the supplied steam profile.

/murder-miners lobbies – Finds all Lobbies currently publically open in Murder Miners.

Miner Bot 2.0 Permissions
Permissions Miner Bot needs to function.

Add a bot to a server – To Add Miner Bot to your server.

Create Commands – Allows Miner Bot to Add Slash Commands in to your discord server.

Manage access to its commands in a server – Lets Miner Bot to make changes to its Slash Commands.

Administrator – Optional, to message your administrators on new updates, set channel alerts. Some Bot Features use this.

For more information, please visit: https://murderminershub.com/discord-bot/

Created by 24k Connect LLC (24kConnect)



Running the MinerBot:

You can run this in Docker or as a normal application.

Try to use the Latest Version of Visual Studio such as Visual Studio 2026.

Make sure to have all of the credentials listed bellow.

If not already, set MinerBot 2.0 project as the Startup Project in Visual Studio.

You need to create a appsettings.json file with the following code and credentials filled in:

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

And include environment variables or .env file with the following credentials:


"BOTTOKEN": "{Discord Bot Token}",
"WEBHOOKCHANNEL": "{Optional Webhook Channel. Not really needed}",
"TENORKEY": "{Optional Tenor Key. Not really needed.}",
"STEAMWEB_KEY": "{Steam Web API Key}",
"STEAMWEB_PUB_KEY": "{Steam Publisher Key (Only for MM Devs who have Steam Developer Access)}",
"STEAMUSER_NAME": "{Your Steam User Name}",
"STEAMUSER_PASSWORD": "{Your Steam Password}",
"STEAM_QRCODE_AUTH": "true",
"STEAMUSER_REMEMBERME": "true",
"STEAMAPP_ID": "274900"

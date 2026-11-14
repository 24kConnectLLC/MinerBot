using Discord.Interactions;
using Discord.WebSocket;
using Discord;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Attributes
{
    internal class RateLimitAttribute : PreconditionAttribute
    {
        // One entry per id (a user, channel or guild, or 0 for everyone) and command, holding when each use that still
        // counts stops counting. The arrays are only ever replaced, never changed, so every reader sees a whole one.
        public static ConcurrentDictionary<(ulong Id, string Command), DateTime[]> Items = new();
        private static DateTime _removeExpiredCommandsTime = DateTime.MinValue;
        private static readonly object _removeExpiredCommandsLock = new();
        private readonly RateLimitType? _context;
        private readonly RateLimitBaseType _baseType;
        private readonly int _requests;
        private readonly int _seconds;
        public RateLimitAttribute(int seconds = 4, int requests = 1, RateLimitType context = RateLimitType.User, RateLimitBaseType baseType = RateLimitBaseType.BaseOnCommandInfo)
        {
            this._context = context;
            this._requests = requests;
            this._seconds = seconds;
            this._baseType = baseType;
        }
        public override Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
        {
            var now = DateTime.UtcNow;

            // clear old expired commands every 30m
            if (IsCleanupDue(now))
                ClearExpiredCommands(now);

            ulong id = _context.Value switch
            {
                RateLimitType.User => context.User.Id,
                RateLimitType.Channel => context.Channel.Id,
                RateLimitType.Guild => context.Guild.Id,
                RateLimitType.Global => 0,
                _ => 0
            };

            var contextId = _baseType switch
            {
                RateLimitBaseType.BaseOnCommandInfo => commandInfo.Module.Name + "//" + commandInfo.Name + "//" + commandInfo.MethodName,
                RateLimitBaseType.BaseOnCommandInfoHashCode => commandInfo.GetHashCode().ToString(),
                RateLimitBaseType.BaseOnSlashCommandName => (context.Interaction as SocketSlashCommand).CommandName,
                RateLimitBaseType.BaseOnMessageComponentCustomId => (context.Interaction as SocketMessageComponent).Data.CustomId,
                RateLimitBaseType.BaseOnAutocompleteCommandName => (context.Interaction as SocketAutocompleteInteraction).Data.CommandName,
                RateLimitBaseType.BaseOnApplicationCommandName => (context.Interaction as SocketApplicationCommand).Name,
                _ => "unknown"
            };

            if (TryUse((id, contextId), now, out var usableAt))
                return Task.FromResult(PreconditionResult.FromSuccess());

            // A refused click is about the buttons, not the command that made them, which has its own cooldown
            var refused = context.Interaction is IComponentInteraction ? "You can use these buttons again" : "This command is usable";
            return Task.FromResult(PreconditionResult.FromError($"{refused} <t:{((DateTimeOffset)usableAt).ToUnixTimeSeconds()}:R>."));
        }

        // The decision and the use it adds are stored in one step, so two uses at the same moment can't both get through.
        // AddOrUpdate can run a factory more than once when two uses race, and keeps only the result of the last run,
        // which also set allowed, so the decision always matches what was stored.
        internal bool TryUse((ulong Id, string Command) key, DateTime now, out DateTime usableAt)
        {
            bool allowed = false;

            var uses = Items.AddOrUpdate(key, _ => Decide([]), (_, previous) => Decide(previous));

            usableAt = uses.LastOrDefault();
            return allowed;

            DateTime[] Decide(DateTime[] previous)
            {
                var current = previous.Where(expireAt => now < expireAt).ToArray();
                allowed = current.Length < _requests;
                return allowed ? [.. current, now.AddSeconds(_seconds)] : current;
            }
        }

        // Checked and pushed forward together, so two uses can't both start a cleanup
        private static bool IsCleanupDue(DateTime now)
        {
            lock (_removeExpiredCommandsLock)
            {
                if (now <= _removeExpiredCommandsTime)
                    return false;

                _removeExpiredCommandsTime = now.AddMinutes(30);
                return true;
            }
        }

        public static Task ClearExpiredCommands() => ClearExpiredCommands(DateTime.UtcNow);

        // Removing the pair only removes the entry while it still holds the array that was checked,
        // so a use that lands in the meantime stays.
        internal static Task ClearExpiredCommands(DateTime now)
        {
            foreach (var item in Items)
                if (item.Value.All(expireAt => now >= expireAt))
                    Items.TryRemove(item);

            return Task.CompletedTask;
        }
        public enum RateLimitType
        {
            User,
            Channel,
            Guild,
            Global
        }
        public enum RateLimitBaseType
        {
            BaseOnCommandInfo,
            BaseOnCommandInfoHashCode,
            BaseOnSlashCommandName,
            BaseOnMessageComponentCustomId,
            BaseOnAutocompleteCommandName,
            BaseOnApplicationCommandName,
            BaseOnApplicationCommandId,
        }
    }
}

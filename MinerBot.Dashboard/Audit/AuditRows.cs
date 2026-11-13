using System.Globalization;
using System.Text.Json;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Audit
{
    // One field of a row's JSON: its label, and its value as text, or null for none
    public sealed record AuditField(string Label, string? Value);

    // A row's JSON before or after the change. An object's fields each get a label; anything else, or JSON that doesn't
    // parse, is the stored text as it is, cut short.
    public sealed record AuditChange(IReadOnlyList<AuditField> Fields, string? Text);

    // What a row is about, in words, with a link only when its ID parsed as one the dashboard has a page for
    public sealed record AuditTarget(string Text, string? Link);

    // One audit row as the pages show it. NoActor is a person Discord didn't name, such as a sign-in refused for its
    // permissions before Discord said who it was. Ip is null unless the page shows addresses, which only the Owner's audit
    // page does, so for anyone else it isn't in the model at all.
    public sealed record AuditRow(DateTime At, ulong? ActorId, string? ActorName, string Role, bool NoActor, string Action, string Outcome,
        ulong? GuildId, AuditTarget? Target, AuditChange? Before, AuditChange? After, string? Detail, string? Ip);

    // The rows the shared partial draws, and whether it has the address column
    public sealed record AuditTable(IReadOnlyList<AuditRow> Rows, bool ShowIp);

    internal static class AuditRows
    {
        // The most of one value a page shows: the JSON can hold 16,000 characters, which nobody reads in a table
        public const int ValueLimit = 300;

        public static AuditTable Table(IEnumerable<AuditEntry> entries, bool showIp) => new(entries.Select(entry => Row(entry, showIp)).ToList(), showIp);

        // MySql.Data reads the time back with no kind, and the dashboard writes it in UTC
        public static AuditRow Row(AuditEntry entry, bool showIp) => new(
            DateTime.SpecifyKind(entry.at, DateTimeKind.Utc),
            entry.actor_id,
            entry.actor_name,
            AuditWords.RoleLabel(entry.actor_role),
            entry.actor_id == null && entry.actor_role is not (AuditRole.Bot or AuditRole.System),
            AuditWords.ActionLabel(entry.action),
            AuditWords.OutcomeLabel(entry.outcome),
            entry.guild_id,
            Target(entry.target_type, entry.target_id),
            Change(entry.before_json),
            Change(entry.after_json),
            entry.detail,
            showIp ? entry.ip : null);

        // Every link is made from the number the ID parsed as, never from the stored text, so a row someone wrote by hand
        // can't make the page link anywhere else
        internal static AuditTarget? Target(string? type, string? id)
        {
            if (type == null && id == null)
                return null;

            if (type == AuditWords.ArmorTarget && long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var armor))
            {
                var number = armor.ToString(CultureInfo.InvariantCulture);
                return new AuditTarget("Armor " + number, "/admin/armor/" + number);
            }

            if (type == AuditWords.BlacklistTarget && uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var account))
                return new AuditTarget("Blacklist entry " + SteamAccounts.Steam3(account), SteamAccounts.EntryLink(account));

            // Such as a session, a number no account can be, or a kind of target a later version writes
            var label = AuditWords.TargetLabel(type);
            return new AuditTarget(string.IsNullOrEmpty(id) ? label : $"{label} {id}".Trim(), null);
        }

        // Read with a parser that throws for anything that isn't JSON, rather than shown raw: System.Text.Json writes every
        // character outside ASCII as an escape, which would leave a name in Arabic or with emoji unreadable
        internal static AuditChange? Change(string? json)
        {
            if (json == null)
                return null;

            try
            {
                using var document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new AuditChange([], Cut(json));

                return new AuditChange(document.RootElement.EnumerateObject().Select(field => new AuditField(Label(field.Name), Value(field.Name, field.Value))).ToList(), null);
            }
            // JsonDocument takes an escaped half of a character, such as \ud800, which reading the name or the value as text
            // then throws for, so that's JSON that can't be read too. Every read is inside this try, through the ToList.
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
            {
                return new AuditChange([], Cut(json));
            }
        }

        // The keys the dashboard writes: armor's status and ticked warnings, and a blacklist entry's fields
        static string Label(string key) => key switch
        {
            "Status" => "Status",
            "Ticked" => "Warnings ticked",
            BlacklistJson.Name => "Name",
            BlacklistJson.Reason => "Reason",
            BlacklistJson.Expires => "End date",
            BlacklistJson.Notes => "Notes",
            _ => key
        };

        static string? Value(string key, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => Cut(value.GetString() ?? ""),
            JsonValueKind.Array => Cut(string.Join(", ", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? Item(key, item.GetString() ?? "") : item.GetRawText()))),
            _ => Cut(value.GetRawText())
        };

        // A ticked warning, such as HiddenCharacters:Name, in the armor pages' own words when both halves are names they
        // know exactly, as ArmorForm checks a tick
        static string Item(string key, string item)
        {
            if (key == "Ticked" && item.Split(':') is [var kind, var field]
                && Enum.GetNames<ArmorProblemKind>().Contains(kind) && Enum.GetNames<ArmorField>().Contains(field))
                return $"{ArmorWords.Label(Enum.Parse<ArmorProblemKind>(kind))} ({ArmorWords.Field(Enum.Parse<ArmorField>(field))})";

            return item;
        }

        // Never half a character
        internal static string Cut(string text)
        {
            if (text.Length <= ValueLimit)
                return text;

            var length = char.IsHighSurrogate(text[ValueLimit - 1]) ? ValueLimit - 1 : ValueLimit;
            return text[..length] + "…";
        }
    }
}

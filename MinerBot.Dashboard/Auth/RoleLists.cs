using DataAccessLibrary.Audit;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Auth
{
    internal enum DashboardRole
    {
        None,
        Admin,
        Owner
    }

    internal static class DashboardRoles
    {
        // The words the role claims and the audit log use
        public static string Word(this DashboardRole role) => role switch
        {
            DashboardRole.Owner => AuditRole.Owner,
            DashboardRole.Admin => AuditRole.Admin,
            _ => AuditRole.Visitor
        };

        // For people to read
        public static string Name(this DashboardRole role) => role switch
        {
            DashboardRole.Owner => "Owner",
            DashboardRole.Admin => "Admin",
            _ => "none"
        };
    }

    // Dashboard:Owners and Dashboard:Admins, read on every request through IOptionsMonitor, so a change applies without a
    // rebuild and someone taken off a list loses access at their next request
    internal sealed class RoleLists(IOptionsMonitor<DashboardOptions> settings)
    {
        sealed record Lists(DashboardOptions Source, IReadOnlySet<ulong> Owners, IReadOnlySet<ulong> Admins);

        Lists? lists;

        public DashboardRole RoleOf(ulong userId)
        {
            var current = Current();

            // Validation already refuses an ID in both lists, so this order only matters if that check ever went
            if (current.Owners.Contains(userId))
                return DashboardRole.Owner;

            return current.Admins.Contains(userId) ? DashboardRole.Admin : DashboardRole.None;
        }

        // Parsed once for each options instance, which a reload replaces. A list that no longer passes validation throws
        // from CurrentValue, so signed-in pages fail closed until it's fixed.
        Lists Current()
        {
            var options = settings.CurrentValue;
            var cached = Volatile.Read(ref lists);

            if (cached != null && ReferenceEquals(cached.Source, options))
                return cached;

            DiscordIds.TryParse(options.Owners, out var owners, out _);
            DiscordIds.TryParse(options.Admins, out var admins, out _);

            var fresh = new Lists(options, owners, admins);
            Volatile.Write(ref lists, fresh);
            return fresh;
        }
    }
}

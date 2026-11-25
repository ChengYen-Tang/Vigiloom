using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.Resources.Persistence;

/// <summary>Copies device and group data into the independent resource database.</summary>
public static class ResourcesDataMigration
{
    private static readonly SqlTableDataMigration.TableMapping[] Tables =
    [
        new("resources", "Devices", "resources", "Devices"),
        new("resources", "Groups", "resources", "Groups"),
        new("resources", "DeviceTargets", "resources", "DeviceTargets"),
        new("resources", "DeviceAudits", "resources", "DeviceAudits"),
        new("resources", "DeviceGroups", "resources", "DeviceGroups"),
        new("resources", "ServiceGroups", "resources", "ServiceGroups"),
        new("service", "Candidates", "resources", "ApplicationCandidates"),
        new("service", "Services", "resources", "ApplicationServices"),
        new("agent", "Packages", "resources", "AgentPackages")
    ];

    public static Task CopyAsync(string sourceConnection, string targetConnection, CancellationToken token = default) =>
        SqlTableDataMigration.CopyAsync(sourceConnection, targetConnection, Tables, token);
}

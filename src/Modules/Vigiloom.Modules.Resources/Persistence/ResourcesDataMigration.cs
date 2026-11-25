using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.Resources.Persistence;

/// <summary>Copies device and group data into the independent resource database.</summary>
public static class ResourcesDataMigration
{
    private static readonly string[] Tables = ["Devices", "Groups", "DeviceTargets", "DeviceAudits", "DeviceGroups", "ServiceGroups"];

    public static Task CopyAsync(string sourceConnection, string targetConnection, CancellationToken token = default) =>
        SqlTableDataMigration.CopyAsync(sourceConnection, targetConnection, "resources", Tables, token);
}

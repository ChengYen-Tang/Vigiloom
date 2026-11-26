using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Persistence;

/// <summary>Copies network-owned data after the legacy monitoring workers are stopped.</summary>
public static class NetworkDataMigration
{
    private static readonly SqlTableDataMigration.TableMapping[] Tables =
    [
        new("network", "SpeedSettings", "network", "SpeedSettings"),
        new("network", "SpeedRuns", "network", "SpeedRuns"),
        new("network", "PingSettings", "network", "PingSettings"),
        new("network", "PingTargets", "network", "PingTargets", OptionalSourceColumns: ["LastTransitionGeneration"]),
        new("network", "PingSamples", "network", "PingSamples"),
        new("network", "PingRollups", "network", "PingRollups"),
        new("network", "PingStateEvents", "network", "PingStateEvents")
    ];

    public static Task CopyAsync(string sourceConnection, string targetConnection, CancellationToken token = default) =>
        SqlTableDataMigration.CopyAsync(sourceConnection, targetConnection, Tables, token, async (_, target, transaction, ct) =>
        {
            // Resource rowversions are regenerated in the new database. Force reconciliation against
            // the new version space while preserving the generation baseline shared with Operations.
            await using var command = new SqlCommand("UPDATE network.PingTargets SET LastTransitionGeneration = Generation, DeviceVersion = 0x0000000000000000, LeaseUntil = NULL", target, transaction);
            await command.ExecuteNonQueryAsync(ct);
        });
}

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
            // Operations copies the source target generations as its cursor at the same stopped-worker cutover.
            await using var command = new SqlCommand("UPDATE network.PingTargets SET LastTransitionGeneration = Generation", target, transaction);
            await command.ExecuteNonQueryAsync(ct);
        });
}

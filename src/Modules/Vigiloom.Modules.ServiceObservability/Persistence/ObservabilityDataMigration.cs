using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Persistence;

/// <summary>Moves application observation data after the legacy observation writers have stopped.</summary>
public static class ObservabilityDataMigration
{
    private static readonly SqlTableDataMigration.TableMapping[] Tables =
    [
        new("service", "Sources", "service", "Sources", OptionalSourceColumns: ["IsSelf"]),
        new("logquota", "TracePolicies", "logquota", "TracePolicies"),
        new("service", "Candidates", "service", "Candidates"),
        new("service", "Services", "service", "Services"),
        new("service", "Instances", "service", "Instances"),
        new("service", "InstancePolicies", "service", "InstancePolicies"),
        new("service", "Collectors", "service", "Collectors"),
        new("service", "CollectorRoutes", "service", "CollectorRoutes"),
        new("service", "ReceptionRejections", "service", "ReceptionRejections"),
        new("service", "HealthNetworkPolicies", "service", "HealthNetworkPolicies"),
        new("service", "HealthNetworkRevisions", "service", "HealthNetworkRevisions"),
        new("service", "HealthTargets", "service", "HealthTargets"),
        new("service", "HealthSchedules", "service", "HealthSchedules", OptionalSourceColumns: ["LastTransitionGeneration"]),
        new("service", "HealthObservations", "service", "HealthObservations"),
        new("service", "HealthStateIntervals", "service", "HealthStateIntervals"),
        new("service", "TelemetryRetentionSettings", "service", "TelemetryRetentionSettings"),
        new("service", "TraceContexts", "service", "TraceContexts"),
        new("service", "TraceSpans", "service", "TraceSpans"),
        new("service", "MetricContexts", "service", "MetricContexts"),
        new("service", "MetricPoints", "service", "MetricPoints"),
        new("service", "MetricRollupWindows", "service", "MetricRollupWindows"),
        new("service", "MetricRollupSeries", "service", "MetricRollupSeries")
    ];

    public static Task CopyAsync(string source, string target, CancellationToken token = default) =>
        SqlTableDataMigration.CopyAsync(source, target, Tables, token, HealthVersionMigration.CopyAsync, RemoveInitialCountersAsync);

    private static async Task RemoveInitialCountersAsync(SqlConnection target, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            IF (SELECT COUNT(*) FROM service.ReceptionRejections) <> 3 OR EXISTS
                (SELECT 1 FROM service.ReceptionRejections WHERE Kind NOT IN (1, 2, 3) OR ObservedCount <> 0
                    OR FirstRecordedAt IS NOT NULL OR LastRecordedAt IS NOT NULL OR ExampleIngress IS NOT NULL OR ExampleSourceId IS NOT NULL)
                THROW 50000, 'The target rejection counters are not the initial values.', 1;
            DELETE FROM service.ReceptionRejections;
            """, target, transaction);
        await command.ExecuteNonQueryAsync(token);
    }
}

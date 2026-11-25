using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

/// <summary>Copies existing operations and Log data into the independent Operations database.</summary>
public static class OperationsDataMigration
{
    private static readonly SqlTableDataMigration.TableMapping[] Tables =
    [
        new("operations", "AuditEvents", "operations", "AuditEvents"),
        new("operations", "AuditRetentionSettings", "operations", "AuditRetentionSettings"),
        new("operations", "PingAlerts", "operations", "PingAlerts"),
        new("operations", "PingAlertEvents", "operations", "PingAlertEvents"),
        new("operations", "Notifications", "operations", "Notifications"),
        new("operations", "ServiceHealthAlerts", "operations", "ServiceHealthAlerts"),
        new("operations", "LineSettings", "operations", "LineSettings"),
        new("operations", "LineGroups", "operations", "LineGroups"),
        new("operations", "LineDeliveries", "operations", "LineDeliveries"),
        new("logquota", "Policies", "logquota", "Policies"),
        new("logquota", "Buckets", "logquota", "Buckets"),
        new("logquota", "TracePolicies", "logquota", "TracePolicies"),
        new("logquota", "TraceBuckets", "logquota", "TraceBuckets"),
        new("agent", "HostLogs", "logs", "HostLogs"),
        new("service", "LogContexts", "logs", "LogContexts"),
        new("service", "LogRecords", "logs", "LogRecords")
    ];

    public static Task CopyAsync(string sourceConnection, string targetConnection, CancellationToken token = default) =>
        SqlTableDataMigration.CopyAsync(sourceConnection, targetConnection, Tables, token);
}

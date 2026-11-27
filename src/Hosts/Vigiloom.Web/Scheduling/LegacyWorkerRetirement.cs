using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.Scheduling;

namespace Vigiloom.Web.Scheduling;

/// <summary>Retires superseded recurring triggers after their owning service registers its replacement.</summary>
internal sealed class LegacyWorkerRetirement(IConfiguration configuration, JobStorage storage, IBackgroundJobClient jobs)
{
    private static readonly Dictionary<string, (string Schedule, string Queue)> Owners = new(StringComparer.Ordinal)
    {
        ["AutomaticSpeedJob"] = ("vigiloom-speed-test", "network"),
        ["PingRetentionJob"] = ("vigiloom-ping-retention", "network"),
        ["PingRollupBackfillJob"] = ("vigiloom-ping-rollup-backfill", "network"),
        ["SpeedRetentionJob"] = ("vigiloom-speed-retention", "network"),
        ["AgentInboxRetentionJob"] = ("vigiloom-host-inbox-retention", "host"),
        ["HostMetricRollupJob"] = ("vigiloom-host-service-metric-rollup", "host"),
        ["HostMetricRetentionJob"] = ("vigiloom-host-service-metric-retention", "host"),
        ["MetricRollupJob"] = ("vigiloom-metric-rollup", "observability"),
        ["TelemetryRetentionJob"] = ("vigiloom-telemetry-retention", "observability"),
        ["AuditRetentionJob"] = ("vigiloom-operations-retention", "operations"),
        ["LineDeliveryJob"] = ("vigiloom-operations-retention", "operations"),
        ["InventoryExpiryJob"] = ("vigiloom-security-inventory-expiry", "security"),
        ["IntegrityExpiryJob"] = ("vigiloom-security-integrity-expiry", "security"),
        ["IntelligenceRefreshJob"] = ("vigiloom-security-intelligence-refresh", "security"),
        ["DebianRefreshJob"] = ("vigiloom-security-debian-refresh", "security"),
        ["UbuntuRefreshJob"] = ("vigiloom-security-ubuntu-osv-refresh", "security"),
        ["MsrcRefreshJob"] = ("vigiloom-security-msrc-cvrf-refresh", "security"),
        ["EpssRefreshJob"] = ("vigiloom-security-first-epss-refresh", "security"),
        ["LinuxReassessmentJob"] = ("vigiloom-security-linux-reassessment", "security"),
        ["WindowsReassessmentJob"] = ("vigiloom-security-windows-reassessment", "security"),
        ["AdvisoryRefreshJob"] = ("vigiloom-security-advisory-refresh", "security"),
        ["SigmaRefreshJob"] = ("vigiloom-security-sigma-refresh", "security"),
        ["SignatureRefreshJob"] = ("vigiloom-security-signature-refresh", "security"),
        ["BaselineRefreshJob"] = ("vigiloom-security-baseline-refresh", "security")
    };
    private const string Prefix = "Vigiloom.Worker.Hosting.";

    internal async Task<bool> RunAsync(CancellationToken token)
    {
        using var connection = storage.GetConnection();
        using var gate = connection.AcquireDistributedLock("vigiloom:legacy-worker-retirement", TimeSpan.FromSeconds(15));
        var schedules = connection.GetRecurringJobs().Where(item => item.Job is not null)
            .ToDictionary(item => item.Id, item => item.Job.Queue, StringComparer.Ordinal);
        await using var sql = new SqlConnection(SchedulingRegistration.HangfireConnection(configuration));
        await sql.OpenAsync(token);
        await using (var recurring = sql.CreateCommand())
        {
            recurring.CommandText = "SELECT COUNT(*) FROM [hangfire].[Hash] WHERE [Field] = 'Job' AND [Key] LIKE 'recurring-job:%' AND [Value] LIKE '%Vigiloom.Worker.Hosting.%'";
            if (Convert.ToInt32(await recurring.ExecuteScalarAsync(token)) != 0) return false;
        }
        var waiting = false;
        long last = 0;
        while (true)
        {
            var batch = new List<(long Id, string Payload, string State)>();
            await using (var read = sql.CreateCommand())
            {
                read.CommandText = "SELECT TOP (200) [Id], [InvocationData], [StateName] FROM [hangfire].[Job] WHERE [Id] > @last AND [StateName] IN ('Enqueued', 'Scheduled') AND [InvocationData] LIKE '%Vigiloom.Worker.Hosting.%' ORDER BY [Id]";
                read.Parameters.AddWithValue("@last", last);
                await using var reader = await read.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) batch.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
            if (batch.Count == 0) break;
            foreach (var item in batch)
            {
                last = item.Id;
                var invocation = InvocationData.DeserializePayload(item.Payload);
                var typeParts = invocation.Type.Split(',');
                var name = typeParts[0].Trim();
                if (typeParts.Length < 2 || !string.Equals(typeParts[1].Trim(), "Vigiloom.Worker", StringComparison.Ordinal) ||
                    !name.StartsWith(Prefix, StringComparison.Ordinal) || invocation.Method != "RunAsync" ||
                    !Owners.TryGetValue(name[Prefix.Length..], out var owner) ||
                    !IsOwnerEnabled(owner.Queue) ||
                    !schedules.TryGetValue(owner.Schedule, out var queue) || queue != owner.Queue)
                {
                    waiting = true;
                    continue;
                }
                if (!jobs.ChangeState(item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new DeletedState { Reason = "Superseded by the owning service's recurring job." }, item.State))
                {
                    waiting = true;
                    continue;
                }
                await using var remove = sql.CreateCommand();
                remove.CommandText = "DELETE queue FROM [hangfire].[JobQueue] AS queue INNER JOIN [hangfire].[Job] AS job ON job.[Id] = queue.[JobId] WHERE queue.[JobId] = @id AND queue.[FetchedAt] IS NULL AND job.[StateName] = 'Deleted'";
                remove.Parameters.AddWithValue("@id", item.Id);
                await remove.ExecuteNonQueryAsync(token);
            }
        }
        return !waiting;
    }

    private bool IsOwnerEnabled(string queue) => queue switch
    {
        "network" => !string.IsNullOrWhiteSpace(configuration["Services:Network:Url"]),
        "host" => !string.IsNullOrWhiteSpace(configuration["Services:HostMonitoring:Url"]),
        "observability" => !string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]),
        "operations" => !string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]),
        "security" => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]) && configuration.GetValue("Services:Security:IntelligenceEnabled", false),
        _ => false
    };
}

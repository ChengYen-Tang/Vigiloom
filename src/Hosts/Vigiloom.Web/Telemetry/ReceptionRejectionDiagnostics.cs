using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Telemetry;

internal enum ReceptionRejectionKind : byte
{
    CredentialRejected = 1,
    SourceUnavailable = 2,
    ServiceNotAuthorized = 3
}

internal enum ReceptionIngress : byte { Discovery = 1, Otlp = 2 }

/// <summary>Fixed-size, best-effort diagnostic aggregation; no request content is retained.</summary>
internal sealed class ReceptionRejectionDiagnostics(
    IDbContextFactory<ServiceObservabilityDbContext> contexts,
    ILogger<ReceptionRejectionDiagnostics> logger) : BackgroundService
{
    private readonly Bucket[] buckets = [new(), new(), new()];

    internal void Record(ReceptionRejectionKind kind, ReceptionIngress ingress, Guid? authenticatedSourceId = null)
    {
        var index = (int)kind - 1;
        if ((uint)index >= buckets.Length) throw new ArgumentOutOfRangeException(nameof(kind));
        if (ingress is not (ReceptionIngress.Discovery or ReceptionIngress.Otlp)) throw new ArgumentOutOfRangeException(nameof(ingress));
        if (kind == ReceptionRejectionKind.CredentialRejected && authenticatedSourceId is not null) throw new ArgumentException("Unverified credentials cannot identify a source.", nameof(authenticatedSourceId));
        var bucket = buckets[index];
        lock (bucket)
        {
            if (bucket.Count < long.MaxValue) bucket.Count++;
            bucket.ExampleIngress = ingress;
            bucket.ExampleSourceId = authenticatedSourceId;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await FlushAsync(stoppingToken);
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < buckets.Length; i++)
        {
            long delta;
            byte ingress;
            Guid? sourceId;
            var bucket = buckets[i];
            lock (bucket)
            {
                delta = bucket.Count;
                ingress = (byte)bucket.ExampleIngress;
                sourceId = bucket.ExampleSourceId;
                bucket.Count = 0;
                bucket.ExampleSourceId = null;
            }
            if (delta == 0) continue;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await using var db = await contexts.CreateDbContextAsync(deadline.Token);
                var kind = (byte)(i + 1);
                var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [service].[ReceptionRejections]
                    SET [ObservedCount] = CASE WHEN [ObservedCount] > 9223372036854775807 - {delta} THEN 9223372036854775807 ELSE [ObservedCount] + {delta} END,
                        [FirstRecordedAt] = COALESCE([FirstRecordedAt], TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                        [LastRecordedAt] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                        [ExampleIngress] = {ingress}, [ExampleSourceId] = {sourceId}
                    WHERE [Kind] = {kind}
                    """, deadline.Token);
                if (affected != 1) logger.LogWarning("Reception rejection summary row is missing for kind {Kind}", kind);
            }
            catch (Exception ex)
            {
                // A timed-out commit has an unknown outcome. Never replay the delta and double-count it.
                logger.LogWarning("Reception rejection summary could not be saved: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    private sealed class Bucket
    {
        public long Count;
        public ReceptionIngress ExampleIngress;
        public Guid? ExampleSourceId;
    }
}

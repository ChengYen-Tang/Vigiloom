using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public enum SpeedRequestStatus { Accepted, ConsentRequired, Busy, NotDue }
public sealed record SpeedRequestResult(SpeedRequestStatus Status, Guid? RunId = null);
public sealed record SpeedLease(Guid Id, Guid LeaseId, int MaxMiB, int TimeoutSeconds);
public sealed class SpeedQueue(IDbContextFactory<NetworkDbContext> contexts)
{
    public async Task<SpeedRequestResult> RequestAsync(Guid requestId, Guid? actor, bool automatic, CancellationToken token)
    {
        if (requestId == Guid.Empty || (!automatic && (actor is null || actor == Guid.Empty))) throw new ArgumentException("A request identifier and authenticated actor are required.");
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var settings = await SpeedDatabase.LockAsync(db, token);
        var existing = await db.SpeedRuns.AsNoTracking().SingleOrDefaultAsync(x => x.RequestedBy == actor && x.RequestId == requestId, token);
        if (existing is not null) return new(SpeedRequestStatus.Accepted, existing.Id);
        var now = await SpeedDatabase.NowAsync(db, token);
        await RecoverAsync(db, now, token);
        if (!SpeedDatabase.HasConsent(settings)) return new(SpeedRequestStatus.ConsentRequired);
        if (automatic && (!settings.Automatic || settings.NextAutomaticAt is null || settings.NextAutomaticAt > now)) return new(SpeedRequestStatus.NotDue);
        if (await db.SpeedRuns.AnyAsync(x => x.State == SpeedTestState.Queued || x.State == SpeedTestState.Running, token)) return new(SpeedRequestStatus.Busy);
        var run = new SpeedRun { RequestId = requestId, RequestedBy = actor, Automatic = automatic, RequestedAt = now,
            MaxMiB = settings.MaxMiB, TimeoutSeconds = settings.TimeoutSeconds, State = SpeedTestState.Queued };
        db.SpeedRuns.Add(run);
        if (automatic) settings.NextAutomaticAt = SpeedDatabase.NextAutomatic(now, settings.IntervalHours);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(SpeedRequestStatus.Accepted, run.Id);
    }
    public async Task<SpeedLease?> ClaimAsync(CancellationToken token)
    {
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var settings = await SpeedDatabase.LockAsync(db, token);
        var now = await SpeedDatabase.NowAsync(db, token);
        await RecoverAsync(db, now, token);
        if (!SpeedDatabase.HasConsent(settings)) { await transaction.CommitAsync(token); return null; }
        var run = await db.SpeedRuns.SingleOrDefaultAsync(x => x.State == SpeedTestState.Queued, token);
        if (run is null) { await transaction.CommitAsync(token); return null; }
        run.State = SpeedTestState.Running; run.StartedAt = now; run.LeaseId = ResourceIds.Create();
        run.LeaseUntil = now.AddSeconds(run.TimeoutSeconds + 30);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(run.Id, run.LeaseId.Value, run.MaxMiB, run.TimeoutSeconds);
    }
    public async Task<bool> MayContinueAsync(SpeedLease lease, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await SpeedDatabase.NowAsync(db, token);
        return await db.SpeedSettings.AnyAsync(x => x.Name == "central" && x.ConsentPolicy == SpeedDatabase.ConsentPolicy && x.ConsentedAt != null, token) &&
            await db.SpeedRuns.AnyAsync(x => x.Id == lease.Id && x.LeaseId == lease.LeaseId && x.State == SpeedTestState.Running && !x.CancellationRequested && x.LeaseUntil > now, token);
    }
    public async Task<bool> CancelAsync(Guid id, CancellationToken token)
    {
        await SpeedDatabase.EnsureAsync(contexts, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await SpeedDatabase.LockAsync(db, token);
        var run = await db.SpeedRuns.SingleOrDefaultAsync(x => x.Id == id, token);
        if (run is null) return false;
        if (run.State is SpeedTestState.Queued or SpeedTestState.Running)
        {
            run.CancellationRequested = true;
            if (run.State == SpeedTestState.Queued)
            {
                run.State = SpeedTestState.Cancelled; run.CompletedAt = await SpeedDatabase.NowAsync(db, token); run.DiagnosticCode = "cancelled_before_start";
            }
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token); return true;
    }
    public async Task<bool> CompleteAsync(SpeedLease lease, SpeedMeasurement measurement, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var settings = await SpeedDatabase.LockAsync(db, token);
        var now = await SpeedDatabase.NowAsync(db, token);
        var run = await db.SpeedRuns.SingleOrDefaultAsync(x => x.Id == lease.Id && x.LeaseId == lease.LeaseId && x.State == SpeedTestState.Running && x.LeaseUntil > now, token);
        if (run is null) return false;
        run.State = run.CancellationRequested || !SpeedDatabase.HasConsent(settings) ? SpeedTestState.Cancelled : measurement.State;
        run.Server = measurement.Server; run.City = measurement.City; run.Country = measurement.Country;
        run.Latitude = measurement.Latitude; run.Longitude = measurement.Longitude;
        run.DownloadMbps = measurement.DownloadMbps; run.UploadMbps = measurement.UploadMbps;
        run.LatencyMilliseconds = measurement.LatencyMilliseconds; run.Bytes = measurement.Bytes;
        run.DiagnosticCode = run.State == SpeedTestState.Cancelled ? "cancelled" : measurement.DiagnosticCode;
        run.CompletedAt = now; run.LeaseUntil = null;
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return true;
    }
    private static async Task RecoverAsync(NetworkDbContext db, DateTimeOffset now, CancellationToken token)
    {
        await db.SpeedRuns.Where(x => x.State == SpeedTestState.Running && x.LeaseUntil <= now).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, SpeedTestState.Interrupted).SetProperty(x => x.CompletedAt, now).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(x => x.DiagnosticCode, "worker_lease_expired"), token);
        await db.SpeedRuns.Where(x => x.State == SpeedTestState.Queued && x.RequestedAt < now.AddMinutes(-5)).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.State, SpeedTestState.Interrupted).SetProperty(x => x.CompletedAt, now).SetProperty(x => x.DiagnosticCode, "queue_expired"), token);
    }
}

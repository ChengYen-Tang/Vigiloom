using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

public enum DebianRefreshResult { Staged, Unchanged, NotDue, Disabled, Busy, Conflict, Failed }

/// <summary>Refreshes the Debian source when due without changing its active revision.</summary>
public sealed class DebianRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock, IntelligencePublication publication)
{
    internal const string LicenseEvidence = "Debian Security Tracker public JSON; fetched at runtime; upstream redistribution terms not specified.";

    public async Task<DebianRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return DebianRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return DebianRefreshResult.NotDue;
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, source.Id, token);
        if (lease is null) return DebianRefreshResult.Busy;
        await using (var read = await contexts.CreateDbContextAsync(token))
        {
            source = await read.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token);
            if (!source.Enabled) return DebianRefreshResult.Disabled;
            if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return DebianRefreshResult.NotDue;
        }
        VendorRefreshStatus.PublicationSnapshot before;
        Guid observed;
        DebianStageStatus status;
        try
        {
            before = await VendorRefreshStatus.CaptureAsync(contexts, source, token);
            using var client = clients.CreateClient(IntelligenceHttp.DebianClientName);
            client.Timeout = TimeSpan.FromMinutes(16);
            var content = await new DebianTrackerClient(client).FetchAsync(source.MaximumBytes, token);
            var stage = await new DebianRevisionStage(contexts, clock).StageWithLeaseAsync(source.Id, source.Version, content, LicenseEvidence, lease, token);
            if (stage.Status is DebianStageStatus.Unavailable or DebianStageStatus.Conflict or DebianStageStatus.Busy) return DebianRefreshResult.Conflict;
            if (stage.RevisionId is not { } revision || !await UpdateStatusAsync(source, null, revision, token)) return DebianRefreshResult.Conflict;
            observed = revision;
            status = stage.Status;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or DbException or DbUpdateException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return await UpdateStatusAsync(source, "Debian tracker retrieval or validation failed.", null, token) ? DebianRefreshResult.Failed : DebianRefreshResult.Conflict;
        }
        await VendorRefreshStatus.PublishAsync(contexts, publication, source.Id, observed, before, token);
        return status == DebianStageStatus.Staged ? DebianRefreshResult.Staged : DebianRefreshResult.Unchanged;
    }

    internal async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'debian-security'")
            .SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = DebianRevisionStage.SourceKey, Enabled = false, MaximumBytes = 134217728, MaximumRecords = 500000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }

    private Task<bool> UpdateStatusAsync(IntelligenceSource expected, string? error, Guid? revisionId, CancellationToken token)
        => VendorRefreshStatus.UpdateAsync(contexts, clock, expected, error, revisionId, token);
}

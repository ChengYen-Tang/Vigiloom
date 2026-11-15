using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

public enum EpssRefreshResult { Staged, Unchanged, NotDue, Disabled, Conflict, Failed }

/// <summary>Refreshes retained FIRST EPSS scores; matching and publication are separate operations.</summary>
public sealed class EpssRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock, IntelligencePublication publication)
{
    internal const string LicenseEvidence = "FIRST EPSS Data and Statistics; https://www.first.org/epss/data";

    public async Task<EpssRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return EpssRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return EpssRefreshResult.NotDue;
        VendorRefreshStatus.PublicationSnapshot before;
        Guid observed;
        EpssStageStatus status;
        try
        {
            before = await VendorRefreshStatus.CaptureAsync(contexts, source, token);
            using var client = clients.CreateClient(IntelligenceHttp.EpssClientName);
            client.Timeout = TimeSpan.FromMinutes(4);
            var result = await new EpssSnapshotStage(contexts, clock).StageAsync(source.Id, source.Version, new EpssClient(client), LicenseEvidence, token);
            if (result.Status is EpssStageStatus.Conflict or EpssStageStatus.Unavailable || result.RevisionId is null) return EpssRefreshResult.Conflict;
            if (!await UpdateStatusAsync(source, null, result.RevisionId, token)) return EpssRefreshResult.Conflict;
            observed = result.RevisionId.Value;
            status = result.Status;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or DbException or DbUpdateException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return await UpdateStatusAsync(source, "EPSS retrieval or validation failed.", null, token) ? EpssRefreshResult.Failed : EpssRefreshResult.Conflict;
        }
        await VendorRefreshStatus.PublishAsync(contexts, publication, source.Id, observed, before, token);
        return status == EpssStageStatus.Staged ? EpssRefreshResult.Staged : EpssRefreshResult.Unchanged;
    }

    internal async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'first-epss'").SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = EpssSnapshotStage.SourceKey, Enabled = false, MaximumBytes = 16 * 1024 * 1024, MaximumRecords = 1000000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }

    private Task<bool> UpdateStatusAsync(IntelligenceSource expected, string? error, Guid? observed, CancellationToken token)
        => VendorRefreshStatus.UpdateAsync(contexts, clock, expected, error, observed, token);
}

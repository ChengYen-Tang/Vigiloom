using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

public enum MsrcRefreshResult { Staged, Unchanged, NotDue, Disabled, Conflict, Failed }

/// <summary>Refreshes retained MSRC source documents; matching and publication are separate operations.</summary>
public sealed class MsrcRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock, IntelligencePublication publication)
{
    internal const string LicenseEvidence = "Microsoft Security Update Guide REST API; https://www.microsoft.com/en-us/msrc/faqs-security-update-guide; Microsoft API terms: https://learn.microsoft.com/en-us/legal/microsoft-apis/terms-of-use";

    public async Task<MsrcRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return MsrcRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return MsrcRefreshResult.NotDue;
        VendorRefreshStatus.PublicationSnapshot before;
        Guid observed;
        MsrcStageStatus status;
        try
        {
            before = await VendorRefreshStatus.CaptureAsync(contexts, source, token);
            using var client = clients.CreateClient(IntelligenceHttp.MsrcClientName);
            client.Timeout = TimeSpan.FromMinutes(4);
            var result = await new MsrcSnapshotStage(contexts, clock).StageAsync(source.Id, source.Version, new MsrcDocumentClient(client), LicenseEvidence, token);
            if (result.Status is MsrcStageStatus.Conflict or MsrcStageStatus.Unavailable || result.RevisionId is null) return MsrcRefreshResult.Conflict;
            if (!await UpdateStatusAsync(source, null, result.RevisionId, token)) return MsrcRefreshResult.Conflict;
            observed = result.RevisionId.Value;
            status = result.Status;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or DbException or DbUpdateException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return await UpdateStatusAsync(source, "MSRC document retrieval or validation failed.", null, token) ? MsrcRefreshResult.Failed : MsrcRefreshResult.Conflict;
        }
        await VendorRefreshStatus.PublishAsync(contexts, publication, source.Id, observed, before, token);
        return status == MsrcStageStatus.Staged ? MsrcRefreshResult.Staged : MsrcRefreshResult.Unchanged;
    }

    internal async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'msrc-cvrf'").SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = MsrcSnapshotStage.SourceKey, Enabled = false, MaximumBytes = 512 * 1024 * 1024, MaximumRecords = 100000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }

    private Task<bool> UpdateStatusAsync(IntelligenceSource expected, string? error, Guid? observed, CancellationToken token)
        => VendorRefreshStatus.UpdateAsync(contexts, clock, expected, error, observed, token);
}

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SharpCompress.Common;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

public enum UbuntuRefreshResult { Staged, Unchanged, NotDue, Disabled, Busy, Conflict, Failed }

/// <summary>Retrieves Canonical's OSV archive and stages it independently of source activation.</summary>
public sealed class UbuntuRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock, IntelligencePublication publication)
{
    private static readonly Uri Feed = new("https://security-metadata.canonical.com/osv/osv-all.tar.xz");

    public async Task<UbuntuRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return UbuntuRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return UbuntuRefreshResult.NotDue;
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, source.Id, token);
        if (lease is null) return UbuntuRefreshResult.Busy;
        await using (var read = await contexts.CreateDbContextAsync(token))
        {
            source = await read.IntelligenceSources.AsNoTracking().SingleAsync(x => x.Id == source.Id, token);
            if (!source.Enabled) return UbuntuRefreshResult.Disabled;
            if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return UbuntuRefreshResult.NotDue;
        }
        VendorRefreshStatus.PublicationSnapshot before;
        Guid observed;
        UbuntuStageStatus status;
        try
        {
            before = await VendorRefreshStatus.CaptureAsync(contexts, source, token);
            using var client = clients.CreateClient(IntelligenceHttp.UbuntuClientName);
            client.Timeout = TimeSpan.FromMinutes(4);
            var artifact = await new VendorSnapshotClient(client, Feed).FetchAsync(source.MaximumBytes, token);
            var staged = await new UbuntuRevisionStage(contexts, clock).StageWithLeaseAsync(source.Id, source.Version, artifact, lease, token);
            if (staged.Status is UbuntuStageStatus.Conflict or UbuntuStageStatus.Unavailable) return UbuntuRefreshResult.Conflict;
            if (staged.RevisionId is not { } revision || !await UpdateStatusAsync(source, null, revision, token)) return UbuntuRefreshResult.Conflict;
            observed = revision;
            status = staged.Status;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or SharpCompressException or DbException or DbUpdateException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return await UpdateStatusAsync(source, "Ubuntu OSV retrieval or validation failed.", null, token) ? UbuntuRefreshResult.Failed : UbuntuRefreshResult.Conflict;
        }
        await VendorRefreshStatus.PublishAsync(contexts, publication, source.Id, observed, before, token);
        return status == UbuntuStageStatus.Staged ? UbuntuRefreshResult.Staged : UbuntuRefreshResult.Unchanged;
    }

    internal async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'ubuntu-osv'")
            .SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = UbuntuRevisionStage.SourceKey, Enabled = false, MaximumBytes = 134217728, MaximumRecords = 150000 };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }

    private Task<bool> UpdateStatusAsync(IntelligenceSource expected, string? error, Guid? observedRevisionId, CancellationToken token)
        => VendorRefreshStatus.UpdateAsync(contexts, clock, expected, error, observedRevisionId, token);
}

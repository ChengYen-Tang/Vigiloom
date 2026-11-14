using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

internal enum RepositoryRefreshResult { Disabled, NotDue, Busy, Staged, Unchanged, Failed, Conflict }

internal sealed record RepositoryRefreshSpec(string SourceKey, string MappingVersion, string SchemaVersion,
    string LicenseEvidence, string Name, RepositorySource Repository,
    Func<SecurityAnalysisDbContext, Guid, CancellationToken, Task<int>> CountAsync,
    Func<SecurityAnalysisDbContext, Guid, Stream, string, int, CancellationToken, Task<int>> StageAsync,
    Func<string, byte[], int, byte[]> Manifest,
    Func<Exception, bool> IsLicenseError);

internal sealed class RepositoryRefreshRunner(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients,
    TimeProvider clock, RepositoryRefreshSpec spec)
{
    public async Task<RepositoryRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return RepositoryRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return RepositoryRefreshResult.NotDue;
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, source.Id, token);
        if (lease is null) return RepositoryRefreshResult.Busy;
        await using (var check = await contexts.CreateDbContextAsync(token))
        {
            var latest = await check.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.Id, token);
            if (latest is null || !latest.Enabled || !latest.Version.AsSpan().SequenceEqual(source.Version)) return RepositoryRefreshResult.Conflict;
            if (!IntelligenceRefresh.IsDue(latest, clock.GetUtcNow())) return RepositoryRefreshResult.NotDue;
        }
        var path = Path.Combine(Path.GetTempPath(), $"vigiloom-repository-{Guid.CreateVersion7():N}.tar.gz");
        var owned = false;
        try
        {
            using var client = clients.CreateClient(IntelligenceHttp.RepositoryClientName);
            var downloader = new RepositoryDownload(client, spec.Repository);
            string commit;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                commit = await downloader.ResolveCommitAsync(timeout.Token);
            }
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var existing = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == source.Id && x.UpstreamVersion == commit &&
                    x.SchemaVersion == spec.SchemaVersion && x.MappingVersion == spec.MappingVersion).OrderByDescending(x => x.RowId)
                    .Select(x => new { x.Id, x.Revoked, x.Normalized, x.RecordCount, x.Artifact, x.Digest, x.LicenseEvidence }).FirstOrDefaultAsync(token);
                if (existing is not null)
                {
                    var valid = existing.Normalized && !existing.Revoked && existing.RecordCount > 0 && existing.LicenseEvidence == spec.LicenseEvidence &&
                        SHA256.HashData(existing.Artifact).AsSpan().SequenceEqual(existing.Digest) &&
                        await spec.CountAsync(db, existing.Id, token) == existing.RecordCount &&
                        !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == source.Id && x.Digest == existing.Digest && x.Revoked, token);
                    return await RecordCheckAsync(source, valid ? null : $"{spec.Name} revision is revoked or incomplete.", token)
                        ? valid ? RepositoryRefreshResult.Unchanged : RepositoryRefreshResult.Failed : RepositoryRefreshResult.Conflict;
                }
            }
            byte[] archiveHash;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                owned = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                archiveHash = await downloader.SaveArchiveAsync(commit, file, source.MaximumBytes, timeout.Token);
            }
            return await StageAsync(source, commit, path, archiveHash, token);
        }
        catch (Exception error) when ((error is HttpRequestException or IOException or InvalidDataException || spec.IsLicenseError(error) || error is OperationCanceledException && !token.IsCancellationRequested))
        {
            var reason = spec.IsLicenseError(error) ? $"{spec.Name} license unavailable or changed." : error switch { InvalidDataException => $"{spec.Name} source failed validation.", OperationCanceledException => $"{spec.Name} retrieval timed out.", _ => $"{spec.Name} retrieval failed." };
            return await RecordCheckAsync(source, reason, token) ? RepositoryRefreshResult.Failed : RepositoryRefreshResult.Conflict;
        }
        finally { if (owned) File.Delete(path); }
    }

    private async Task<RepositoryRefreshResult> StageAsync(IntelligenceSource observed, string commit, string path, byte[] archiveHash, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observed.Id}")
            .SingleOrDefaultAsync(token);
        if (source is null || !source.Enabled || source.Key != spec.SourceKey || !source.Version.AsSpan().SequenceEqual(observed.Version)) return RepositoryRefreshResult.Conflict;
        var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = commit, RetrievedAt = clock.GetUtcNow(),
            Digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{spec.SourceKey}-pending:{commit}")), Artifact = [], SchemaVersion = spec.SchemaVersion,
            MappingVersion = spec.MappingVersion, LicenseEvidence = spec.LicenseEvidence };
        db.IntelligenceRevisions.Add(revision);
        await db.SaveChangesAsync(token);
        var revisionId = revision.Id;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var count = await spec.StageAsync(db, revisionId, file, commit, Math.Min(source.MaximumRecords, 50_000), token);
        var manifest = spec.Manifest(commit, archiveHash, count);
        revision.Artifact = manifest;
        revision.Digest = SHA256.HashData(manifest);
        revision.RecordCount = count;
        revision.Normalized = true;
        source.LastCheckedAt = clock.GetUtcNow();
        source.LastSuccessfulCheckAt = source.LastCheckedAt;
        source.LastError = null;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return RepositoryRefreshResult.Staged;
    }

    private async Task<bool> RecordCheckAsync(IntelligenceSource observed, string? reason, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observed.Id}")
            .SingleOrDefaultAsync(token);
        if (current is null || !current.Enabled || current.Key != spec.SourceKey || !current.Version.AsSpan().SequenceEqual(observed.Version)) return false;
        current.LastCheckedAt = clock.GetUtcNow();
        current.LastError = reason;
        if (reason is null) current.LastSuccessfulCheckAt = current.LastCheckedAt;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = {spec.SourceKey}").SingleOrDefaultAsync(token);
        if (source is null) { source = new IntelligenceSource { Key = spec.SourceKey, Enabled = true, MaximumBytes = 64 * 1024 * 1024 }; db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
        await transaction.CommitAsync(token);
        return source;
    }
}

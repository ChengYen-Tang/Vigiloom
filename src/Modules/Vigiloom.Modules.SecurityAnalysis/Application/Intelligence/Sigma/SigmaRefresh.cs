using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

public enum SigmaRefreshResult { Disabled, NotDue, Busy, Staged, Unchanged, Failed, Conflict }

/// <summary>Stages centrally fetched SigmaHQ source rules; staging does not enable rule execution.</summary>
public sealed class SigmaRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock)
{
    internal const string SourceKey = "sigmahq-rules";
    internal const string MappingVersion = "sigma-rule-source-v1";
    internal const string SchemaVersion = "sigma-yaml-source-v1";
    internal const string LicenseEvidence = "SigmaHQ rules: DRL-1.1, subject to individual rule terms; SigmaHQ/sigma@54967d64e6dda2b4daa81f47f7a8bf1f591b68a0";

    public async Task<SigmaRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return SigmaRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return SigmaRefreshResult.NotDue;
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, source.Id, token);
        if (lease is null) return SigmaRefreshResult.Busy;
        await using (var check = await contexts.CreateDbContextAsync(token))
        {
            var latest = await check.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.Id, token);
            if (latest is null || !latest.Enabled || !latest.Version.AsSpan().SequenceEqual(source.Version)) return SigmaRefreshResult.Conflict;
            if (!IntelligenceRefresh.IsDue(latest, clock.GetUtcNow())) return SigmaRefreshResult.NotDue;
        }
        var path = Path.Combine(Path.GetTempPath(), $"vigiloom-sigma-{Guid.CreateVersion7():N}.tar.gz");
        var owned = false;
        try
        {
            using var client = clients.CreateClient(IntelligenceHttp.SigmaClientName);
            var downloader = new SigmaDownload(client);
            string commit;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                commit = await downloader.ResolveCommitAsync(timeout.Token);
            }
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var existing = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == source.Id && x.UpstreamVersion == commit &&
                    x.SchemaVersion == SchemaVersion && x.MappingVersion == MappingVersion).OrderByDescending(x => x.RowId)
                    .Select(x => new { x.Id, x.Revoked, x.Normalized, x.RecordCount, x.Artifact, x.Digest, x.LicenseEvidence }).FirstOrDefaultAsync(token);
                if (existing is not null)
                {
                    var valid = existing.Normalized && !existing.Revoked && existing.RecordCount > 0 && existing.LicenseEvidence == LicenseEvidence &&
                        SHA256.HashData(existing.Artifact).AsSpan().SequenceEqual(existing.Digest) &&
                        await db.SigmaRules.CountAsync(x => x.RevisionId == existing.Id, token) == existing.RecordCount &&
                        !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == source.Id && x.Digest == existing.Digest && x.Revoked, token);
                    return await RecordCheckAsync(source, valid ? null : "Sigma revision is revoked or incomplete.", token)
                        ? valid ? SigmaRefreshResult.Unchanged : SigmaRefreshResult.Failed : SigmaRefreshResult.Conflict;
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
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or SigmaLicenseException or OperationCanceledException && !token.IsCancellationRequested)
        {
            var reason = error switch { SigmaLicenseException => "Sigma license unavailable or changed.", InvalidDataException => "Sigma source failed validation.", OperationCanceledException => "Sigma retrieval timed out.", _ => "Sigma retrieval failed." };
            return await RecordCheckAsync(source, reason, token) ? SigmaRefreshResult.Failed : SigmaRefreshResult.Conflict;
        }
        finally { if (owned) File.Delete(path); }
    }

    private async Task<SigmaRefreshResult> StageAsync(IntelligenceSource observed, string commit, string path, byte[] archiveHash, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observed.Id}")
            .SingleOrDefaultAsync(token);
        if (source is null || !source.Enabled || source.Key != SourceKey || !source.Version.AsSpan().SequenceEqual(observed.Version)) return SigmaRefreshResult.Conflict;
        var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = commit, RetrievedAt = clock.GetUtcNow(),
            Digest = SHA256.HashData(Encoding.UTF8.GetBytes($"sigma-pending:{commit}")), Artifact = [], SchemaVersion = SchemaVersion,
            MappingVersion = MappingVersion, LicenseEvidence = LicenseEvidence };
        db.IntelligenceRevisions.Add(revision);
        await db.SaveChangesAsync(token);
        var revisionId = revision.Id;
        var pending = 0;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var count = await SigmaArchive.ReadAsync(file, commit, Math.Min(source.MaximumRecords, 50_000), async (rulePath, content, metadata, cancellation) =>
        {
            db.SigmaRules.Add(new SigmaRule { RevisionId = revisionId, Path = rulePath, RuleId = metadata.Id, Title = metadata.Title,
                Author = metadata.Author, Status = metadata.Status, License = metadata.License, Product = metadata.Product,
                Category = metadata.Category, Service = metadata.Service, Digest = SHA256.HashData(content), Content = content });
            if (++pending < 16) return;
            await db.SaveChangesAsync(cancellation);
            foreach (var entry in db.ChangeTracker.Entries<SigmaRule>().ToArray()) entry.State = EntityState.Detached;
            pending = 0;
        }, token);
        if (pending != 0) await db.SaveChangesAsync(token);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { format = "vigiloom-sigma-source-v1", commit, archiveSha256 = Convert.ToHexStringLower(archiveHash),
            mapping = MappingVersion, ruleCount = count, coverage = "rules,emerging-threats,threat-hunting; execution unsupported" });
        revision.Artifact = manifest;
        revision.Digest = SHA256.HashData(manifest);
        revision.RecordCount = count;
        revision.Normalized = true;
        source.LastCheckedAt = clock.GetUtcNow();
        source.LastSuccessfulCheckAt = source.LastCheckedAt;
        source.LastError = null;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return SigmaRefreshResult.Staged;
    }

    private async Task<bool> RecordCheckAsync(IntelligenceSource observed, string? reason, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observed.Id}")
            .SingleOrDefaultAsync(token);
        if (current is null || !current.Enabled || current.Key != SourceKey || !current.Version.AsSpan().SequenceEqual(observed.Version)) return false;
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
        var source = await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'sigmahq-rules'").SingleOrDefaultAsync(token);
        if (source is null) { source = new IntelligenceSource { Key = SourceKey, Enabled = true, MaximumBytes = 64 * 1024 * 1024 }; db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
        await transaction.CommitAsync(token);
        return source;
    }
}

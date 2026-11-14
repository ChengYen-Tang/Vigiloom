using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

public enum SigmaRefreshResult { Disabled, NotDue, Busy, Staged, Unchanged, Failed, Conflict }

/// <summary>Stages source files without enabling detection or publication.</summary>
public sealed class SigmaRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock)
{
    internal const string SourceKey = "sigmahq-rules";
    internal const string MappingVersion = "sigma-rule-source-v2";
    internal const string SchemaVersion = "sigma-yaml-source-v1";
    internal const string LicenseEvidence = "SigmaHQ rules: DRL-1.1, subject to individual rule terms; SigmaHQ/sigma@54967d64e6dda2b4daa81f47f7a8bf1f591b68a0";

    private static readonly RepositoryRefreshSpec Spec = new(SourceKey, MappingVersion, SchemaVersion, LicenseEvidence,
        "Sigma", RepositorySource.Sigma,
        (db, revision, token) => db.SigmaRules.CountAsync(x => x.RevisionId == revision, token),
        StageAsync, (commit, hash, count) => JsonSerializer.SerializeToUtf8Bytes(new { format = "vigiloom-sigma-source-v1", commit, archiveSha256 = Convert.ToHexStringLower(hash), mapping = MappingVersion, ruleCount = count, coverage = "rules,emerging-threats,threat-hunting; execution unsupported" }),
        error => error is SigmaLicenseException);

    public async Task<SigmaRefreshResult> RunDueAsync(CancellationToken token) =>
        (SigmaRefreshResult)await new RepositoryRefreshRunner(contexts, clients, clock, Spec).RunDueAsync(token);

    private static async Task<int> StageAsync(SecurityAnalysisDbContext db, Guid revisionId, Stream file,
        string commit, int maximumFiles, CancellationToken token)
    {
        var pending = 0;
        var count = await SigmaArchive.ReadAsync(file, commit, maximumFiles, async (rulePath, content, metadata, cancellation) =>
        {
            db.SigmaRules.Add(new SigmaRule { RevisionId = revisionId, Path = rulePath, RuleId = metadata.Id, Title = metadata.Title,
                Author = metadata.Author, Status = metadata.Status, License = metadata.License, Product = metadata.Product,
                Category = metadata.Category, Service = metadata.Service, ExclusionReason = SigmaLicense.ExclusionReason(rulePath, metadata.Author, metadata.License),
                Digest = SHA256.HashData(content), Content = content });
            if (++pending < 16) return;
            await db.SaveChangesAsync(cancellation);
            foreach (var entry in db.ChangeTracker.Entries<SigmaRule>().ToArray()) entry.State = EntityState.Detached;
            pending = 0;
        }, token);
        if (pending != 0) await db.SaveChangesAsync(token);
        var manifest = await SigmaManifestBuilder.BuildAsync(db, revisionId, commit, token);
        db.SigmaManifests.Add(manifest);
        await db.SaveChangesAsync(token);
        return count;
    }
}

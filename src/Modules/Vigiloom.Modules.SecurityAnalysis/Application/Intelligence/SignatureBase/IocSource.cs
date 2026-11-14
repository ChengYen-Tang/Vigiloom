using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

/// <summary>Reads the reviewed original IOC files without asserting that they were matched.</summary>
public sealed class IocSource : IIocSource
{
    private readonly IDbContextFactory<SecurityAnalysisDbContext> contexts;
    private readonly TimeProvider clock;
    public IocSource(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
    { this.contexts = contexts; this.clock = clock; }

    public async Task<IocSourceSnapshot> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Key == "signature-base", cancellationToken);
        if (source is null) return Empty(IocSourceAvailability.Unavailable);
        if (!source.Enabled) return Empty(IocSourceAvailability.Disabled, source.Id);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == source.Id, cancellationToken);
        if (active is null) return Empty(IocSourceAvailability.NoActiveRevision, source.Id);
        if (!Fresh(source)) return Empty(IocSourceAvailability.Stale, source.Id);
        var assessment = await IocSourceEligibility.AssessAsync(db, source.Id, active.RevisionId, cancellationToken);
        if (assessment is null) return Empty(IocSourceAvailability.Unavailable, source.Id);
        var commit = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.Id == active.RevisionId).Select(x => x.UpstreamVersion).SingleAsync(cancellationToken);
        if (!Fresh(source)) return Empty(IocSourceAvailability.Stale, source.Id);
        return new(IocSourceAvailability.Ready, source.Id, active.RevisionId, Convert.ToBase64String(active.Version), commit,
            assessment.ManifestSha256, assessment.ValidCount, assessment.UnsupportedCount);
    }

    public async Task<IocSourceFile> ReadFileAsync(IocSourceSnapshot selection, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (path is not (IocSourceEligibility.HashPath or IocSourceEligibility.C2Path)) throw new ArgumentException("Not a reviewed IOC source path.", nameof(path));
        if (selection.Availability != IocSourceAvailability.Ready || selection.SourceId is not { } sourceId ||
            selection.RevisionId is not { } revisionId || selection.ActivationVersion is null || selection.Commit is null || selection.ManifestSha256 is null)
            return EmptyFile(IocSourceAvailability.SelectionChanged);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == "signature-base", cancellationToken);
        if (source is null) return EmptyFile(IocSourceAvailability.Unavailable);
        if (!source.Enabled) return EmptyFile(IocSourceAvailability.Disabled);
        if (!Fresh(source)) return EmptyFile(IocSourceAvailability.Stale);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, cancellationToken);
        if (active is null || active.RevisionId != revisionId || Convert.ToBase64String(active.Version) != selection.ActivationVersion)
            return EmptyFile(IocSourceAvailability.SelectionChanged);
        var assessment = await IocSourceEligibility.AssessAsync(db, sourceId, revisionId, cancellationToken);
        if (assessment is null || assessment.ManifestSha256 != selection.ManifestSha256 || assessment.ValidCount != selection.ValidCount ||
            assessment.UnsupportedCount != selection.UnsupportedCount ||
            !await db.IntelligenceRevisions.AnyAsync(x => x.Id == revisionId && x.SourceId == sourceId && x.UpstreamVersion == selection.Commit, cancellationToken))
            return EmptyFile(IocSourceAvailability.Unavailable);
        var file = await db.SignatureFiles.AsNoTracking().SingleAsync(x => x.RevisionId == revisionId && x.Path == path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file.Content), file.Digest)) return EmptyFile(IocSourceAvailability.Unavailable);
        if (!Fresh(source)) return EmptyFile(IocSourceAvailability.Stale);
        var root = "https://github.com/Neo23x0/signature-base/blob/" + selection.Commit + "/";
        return new(IocSourceAvailability.Ready, file.Path, Convert.ToHexStringLower(file.Digest), root + file.Path,
            "DRL-1.1", root + "LICENSE", root + "README.md#license", file.Content);
    }

    private bool Fresh(IntelligenceSource source)
    {
        var now = clock.GetUtcNow();
        return source.LastSuccessfulCheckAt is { } last && last <= now && last >= now.AddSeconds(-source.StaleAfterSeconds);
    }

    private static IocSourceSnapshot Empty(IocSourceAvailability status, Guid? sourceId = null) => new(status, sourceId, null, null, null, null, 0, 0);
    private static IocSourceFile EmptyFile(IocSourceAvailability status) => new(status, null, null, null, null, null, null, null);
}

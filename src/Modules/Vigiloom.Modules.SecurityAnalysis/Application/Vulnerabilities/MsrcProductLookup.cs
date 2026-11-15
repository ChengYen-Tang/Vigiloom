using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record MsrcProductPage(MsrcCandidateStatus Status, IReadOnlyList<MsrcCandidate> Items,
    bool CoverageComplete, bool HasMore, string? NextCve, MsrcCandidateResult? Evidence = null, string? Product = null);

/// <summary>Discovers CVEs by exact vendor product independently of update inventory.</summary>
internal sealed class MsrcProductLookup(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int PageSize = 200;

    internal async Task<MsrcProductPage> FindAsync(Guid sourceId, Guid revisionId, string product, string? afterCve, CancellationToken token,
        MsrcProductPage? previous = null)
    {
        if (sourceId.Version != 7 || revisionId.Version != 7 || string.IsNullOrWhiteSpace(product) || product.Length > 512 ||
            product.Any(char.IsControl) || afterCve is { Length: > 64 } || (afterCve?.Any(char.IsControl) ?? false) ||
            (afterCve is not null && previous is null))
            throw new ArgumentException("Invalid MSRC product request.");
        var lookup = new MsrcCandidateLookup(contexts, clock);
        var initial = await lookup.FindAsync(sourceId, revisionId, [], token);
        if (initial.Status != MsrcCandidateStatus.Ready) return new(initial.Status, [], false, false, null);
        if (previous is not null && (previous.Status != MsrcCandidateStatus.Ready || !previous.HasMore || previous.Product != product || previous.NextCve != afterCve ||
            previous.Evidence is not { Status: MsrcCandidateStatus.Ready, SourceVersion.Length: 8, ActivationVersion.Length: 8 } prior ||
            prior.SourceId != sourceId || prior.RevisionId != revisionId ||
            !prior.SourceVersion.AsSpan().SequenceEqual(initial.SourceVersion) || !prior.ActivationVersion.AsSpan().SequenceEqual(initial.ActivationVersion)))
            return new(MsrcCandidateStatus.Changed, [], false, false, null);
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await MsrcRevisionEligibility.HasProductIndexAsync(db, revisionId, token))
            return new(MsrcCandidateStatus.Unavailable, [], false, false, null);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(product));
        var incomplete = await (from document in db.MsrcDocuments.AsNoTracking()
            join artifact in db.MsrcArtifacts.AsNoTracking() on document.Digest equals artifact.Digest
            where document.RevisionId == revisionId && !artifact.ProductIndexComplete
            select artifact.RowId).AnyAsync(token);
        var query = from index in db.MsrcProductIndexes.AsNoTracking()
            join document in db.MsrcDocuments.AsNoTracking() on index.Digest equals document.Digest
            where document.RevisionId == revisionId && index.ProductHash == hash && index.ProductName == product
            select index.Cve;
        if (afterCve is not null) query = query.Where(x => string.Compare(x, afterCve) > 0);
        var cves = await query.Distinct().OrderBy(x => x).Take(PageSize + 1).ToArrayAsync(token);
        var count = Math.Min(PageSize, cves.Length);
        MsrcCandidateResult evidence;
        string[] selected;
        do
        {
            selected = cves.Take(count).ToArray();
            evidence = await lookup.FindAsync(sourceId, revisionId, selected, token);
            if (evidence.Status != MsrcCandidateStatus.TooLarge || count <= 1) break;
            count = Math.Max(1, count / 2);
        } while (true);
        var more = cves.Length > count;
        if (evidence.Status != MsrcCandidateStatus.Ready) return new(evidence.Status, [], false, false, null);
        if (!initial.SourceVersion!.AsSpan().SequenceEqual(evidence.SourceVersion) ||
            !initial.ActivationVersion!.AsSpan().SequenceEqual(evidence.ActivationVersion) ||
            !await MsrcRevisionEligibility.HasProductIndexAsync(db, revisionId, token) || !await lookup.IsCurrentAsync(evidence, token))
            return new(MsrcCandidateStatus.Changed, [], false, false, null);
        var items = evidence.Items.Where(x => MsrcWindowsProduct.HasProductReference(x, product) != false).ToArray();
        var ambiguous = items.Any(x => MsrcWindowsProduct.HasProductReference(x, product) is null);
        return new(MsrcCandidateStatus.Ready, items, !incomplete && !ambiguous, more, more ? selected[^1] : null, evidence, product);
    }
}

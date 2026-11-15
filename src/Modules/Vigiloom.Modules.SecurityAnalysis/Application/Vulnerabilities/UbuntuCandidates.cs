using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record UbuntuCandidatePage(bool Complete, string? NextAfterAdvisoryId, IReadOnlyList<UbuntuOsvRecord> Records);

/// <summary>Loads bounded package candidates from one retained revision. Activation and freshness must be checked by the caller.</summary>
internal static class UbuntuCandidates
{
    private const long MaximumExpandedBytes = 64L * 1024 * 1024;

    internal static async Task<UbuntuCandidatePage> LoadAsync(SecurityAnalysisDbContext db, Guid sourceId, Guid revisionId,
        string ecosystem, string package, int maximumDocuments, string? afterAdvisoryId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (sourceId == Guid.Empty || revisionId == Guid.Empty || string.IsNullOrWhiteSpace(ecosystem) || ecosystem.Length > 256 ||
            string.IsNullOrWhiteSpace(package) || package.Length > 256 || maximumDocuments is < 1 or > 1000 ||
            afterAdvisoryId is not null && (string.IsNullOrWhiteSpace(afterAdvisoryId) || afterAdvisoryId.Length > 128))
            throw new ArgumentException("A bounded Ubuntu package lookup is required.");
        var eligible = await db.IntelligenceRevisions.AsNoTracking().AnyAsync(r => r.SourceId == sourceId && r.Id == revisionId &&
            r.Normalized && !r.Revoked && r.MappingVersion == UbuntuRevisionStage.MappingVersion &&
            r.SchemaVersion == UbuntuRevisionStage.SchemaVersion && r.LicenseEvidence == UbuntuRevisionStage.LicenseEvidence &&
            !db.IntelligenceRevisions.Any(other => other.SourceId == sourceId && other.Digest == r.Digest && other.Revoked), token);
        if (!eligible) throw new InvalidDataException("Ubuntu source revision is unavailable.");
        var query = from scope in db.UbuntuScopes.AsNoTracking()
            join member in db.UbuntuRevisionDocuments.AsNoTracking() on new { scope.Digest, scope.MappingVersion } equals new { member.Digest, member.MappingVersion }
            join document in db.UbuntuDocuments.AsNoTracking() on new { member.Digest, member.MappingVersion, member.AdvisoryId }
                equals new { document.Digest, document.MappingVersion, document.AdvisoryId }
            where member.RevisionId == revisionId && scope.Ecosystem == ecosystem && scope.SourcePackage == package &&
                (afterAdvisoryId == null || string.Compare(member.AdvisoryId, afterAdvisoryId) > 0)
            orderby member.AdvisoryId
            select new { document.AdvisoryId, document.Digest, document.Compressed, document.OriginalLength };
        var records = new List<UbuntuOsvRecord>();
        long expanded = 0;
        await foreach (var document in query.Take(maximumDocuments + 1).AsAsyncEnumerable().WithCancellation(token))
        {
            if (document.OriginalLength is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid Ubuntu document length.");
            if (records.Count == maximumDocuments || document.OriginalLength > MaximumExpandedBytes - expanded)
                return new(false, records[^1].Id, records);
            var bytes = GC.AllocateUninitializedArray<byte>(document.OriginalLength);
            using var compressed = new MemoryStream(document.Compressed, writable: false);
            await using var decompressor = new BrotliStream(compressed, CompressionMode.Decompress);
            await decompressor.ReadExactlyAsync(bytes, token);
            if (await decompressor.ReadAsync(new byte[1], token) != 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), document.Digest))
                throw new InvalidDataException("Ubuntu document failed its integrity check.");
            var record = UbuntuOsvReader.Read(bytes, document.OriginalLength);
            var matches = record.Packages.Where(x => x.Ecosystem == ecosystem && x.SourcePackage == package).ToArray();
            if (record.Id != document.AdvisoryId || matches.Length == 0) throw new InvalidDataException("Ubuntu document index does not match its contents.");
            records.Add(record with { Packages = matches });
            expanded += document.OriginalLength;
        }
        return new(true, null, records);
    }
}

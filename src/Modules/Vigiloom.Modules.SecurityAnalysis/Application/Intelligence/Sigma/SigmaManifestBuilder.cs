using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

/// <summary>Hashes a deterministic, attributed source subset without loading the rule bodies.</summary>
internal static class SigmaManifestBuilder
{
    internal const string PolicyVersion = "sigma-core-drl-v1";

    internal static async Task<SigmaManifest> BuildAsync(SecurityAnalysisDbContext db, Guid revisionId, string commit, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, PolicyVersion); Append(hash, commit);
        var included = 0;
        var excluded = 0;
        var rows = db.SigmaRules.AsNoTracking().Where(x => x.RevisionId == revisionId).OrderBy(x => x.Path)
            .Select(x => new { x.Path, x.RuleId, x.Author, x.License, x.Status, x.Product, x.Category, x.Service, x.Digest, x.ExclusionReason });
        await foreach (var rule in rows.AsAsyncEnumerable().WithCancellation(token))
        {
            if (rule.ExclusionReason != SigmaLicense.ExclusionReason(rule.Path, rule.Author, rule.License))
                throw new InvalidDataException("Sigma source eligibility does not match the current policy.");
            if (rule.ExclusionReason is not null) excluded++;
            else included++;
            Append(hash, rule.ExclusionReason ?? "included");
            Append(hash, rule.Path); Append(hash, rule.RuleId ?? ""); Append(hash, rule.Author ?? "");
            Append(hash, rule.License ?? "DRL-1.1"); Append(hash, rule.Status ?? ""); Append(hash, rule.Product ?? "");
            Append(hash, rule.Category ?? ""); Append(hash, rule.Service ?? ""); Append(hash, rule.Digest);
        }
        return new SigmaManifest { RevisionId = revisionId, PolicyVersion = PolicyVersion, IncludedCount = included,
            ExcludedCount = excluded, Digest = hash.GetHashAndReset() };
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> buffer = length <= 512 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try { Encoding.UTF8.GetBytes(value, buffer); Append(hash, buffer); }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }
    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length); hash.AppendData(value);
    }
}

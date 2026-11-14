using System.Formats.Tar;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;
using System.Security.Cryptography;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

internal sealed class SigmaLicenseException() : Exception("Sigma repository license is missing or changed.");

/// <summary>Validates a bounded archive without extracting its paths to the local file system.</summary>
internal static class SigmaArchive
{
    internal static async Task<int> ReadAsync(Stream archive, string commit, int maximumRules,
        Func<string, byte[], SigmaRuleMetadata, CancellationToken, Task> accept, CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ruleCount = 0;
        var licenseVerified = false;
        await RepositoryArchive.ReadAsync(archive, "sigma", commit, async (relative, entry, entryToken) =>
        {
            var path = entry.Name;
            if (relative == "LICENSE")
            {
                if (entry.EntryType == TarEntryType.Directory || entry.Length is < 1 or > 65536 || entry.DataStream is null)
                    throw new InvalidDataException("Invalid Sigma repository license.");
                var license = new byte[checked((int)entry.Length)];
                await entry.DataStream.ReadExactlyAsync(license, entryToken);
                licenseVerified = Convert.ToHexStringLower(SHA256.HashData(license)) == "4213ed7e21d9435b0aa0aa7d11016e6ab3e4664fa71738ff507306fadce79597";
                if (!licenseVerified) throw new SigmaLicenseException();
                return;
            }
            var inRuleTree = relative.StartsWith("rules/", StringComparison.Ordinal) || relative.StartsWith("rules-emerging-threats/", StringComparison.Ordinal) ||
                relative.StartsWith("rules-threat-hunting/", StringComparison.Ordinal);
            var fileName = relative[(relative.LastIndexOf('/') + 1)..];
            if (inRuleTree && entry.EntryType != TarEntryType.Directory &&
                (fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Equals("COPYING", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("COPYING.", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".license", StringComparison.OrdinalIgnoreCase))) throw new SigmaLicenseException();
            if (entry.EntryType == TarEntryType.Directory || !inRuleTree || !path.EndsWith(".yml", StringComparison.Ordinal)) return;
            if (++ruleCount > maximumRules || entry.Length is < 1 or > SigmaRuleValidator.MaximumRuleBytes || entry.DataStream is null)
                throw new InvalidDataException("Sigma rules exceed the configured limits.");
            var content = new byte[checked((int)entry.Length)];
            await entry.DataStream.ReadExactlyAsync(content, entryToken);
            SigmaRuleMetadata metadata;
            try { metadata = SigmaRuleValidator.Validate(content); }
            catch (InvalidDataException error) { throw new InvalidDataException($"Invalid Sigma rule: {path}.", error); }
            if (metadata.Id is { Length: > 128 } || metadata.Author is { Length: > 1024 } ||
                metadata.Status is { Length: > 128 } || metadata.License is { Length: > 256 } ||
                metadata.Product is { Length: > 128 } || metadata.Category is { Length: > 128 } || metadata.Service is { Length: > 128 } ||
                metadata.Id is { } id && !ids.Add(id))
                throw new InvalidDataException("Invalid or duplicate Sigma metadata.");
            await accept(relative, content, metadata, entryToken);
        }, token);
        if (!licenseVerified) throw new SigmaLicenseException();
        if (ruleCount == 0) throw new InvalidDataException("Sigma archive contains no rules.");
        return ruleCount;
    }

}

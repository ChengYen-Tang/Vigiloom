using System.Formats.Tar;
using System.Security.Cryptography;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

internal enum SignatureFileKind { Ioc, Yara, LicenseEvidence }
internal sealed class SignatureLicenseException() : Exception("Signature-Base license evidence is missing or changed.");

/// <summary>
/// Retains upstream files for later interpretation; this does not validate YARA,
/// infer per-rule licenses, or make any files eligible for distribution.
/// The caller must roll back accepted files if the archive fails validation.
/// </summary>
internal static class SignatureArchive
{
    private const int MaximumFileBytes = 1024 * 1024;
    internal const string LicenseHash = "8a959b2413d0c83ca1b3ce9153076c649abf2e4bbfebbb493fb24c2074a1eb8a";
    internal const string ReadmeHash = "746dec77d0504667bf0f57196a98e8e9c936a08d834048589f91517674464496";

    internal static async Task<int> ReadAsync(Stream archive, string commit, int maximumFiles,
        Func<string, SignatureFileKind, byte[], byte[], CancellationToken, Task> accept, CancellationToken token)
    {
        var count = 0;
        var hasLicense = false;
        var hasReadme = false;
        var hasIoc = false;
        var hasYara = false;
        await RepositoryArchive.ReadAsync(archive, "signature-base", commit, async (path, entry, cancellation) =>
        {
            if (entry.EntryType == TarEntryType.Directory) return;
            var fileName = path[(path.LastIndexOf('/') + 1)..];
            var evidence = path is "LICENSE" or "README.md" ||
                ((path.StartsWith("iocs/", StringComparison.Ordinal) || path.StartsWith("yara/", StringComparison.Ordinal)) &&
                 (fileName.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase) ||
                  fileName.Equals("COPYING", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("COPYING.", StringComparison.OrdinalIgnoreCase) ||
                  fileName.EndsWith(".license", StringComparison.OrdinalIgnoreCase)));
            SignatureFileKind kind;
            if (evidence) kind = SignatureFileKind.LicenseEvidence;
            else if (path.StartsWith("iocs/", StringComparison.Ordinal)) { kind = SignatureFileKind.Ioc; hasIoc = true; }
            else if (path.StartsWith("yara/", StringComparison.Ordinal)) { kind = SignatureFileKind.Yara; hasYara = true; }
            else return;

            if (++count > maximumFiles || entry.Length is < 1 or > MaximumFileBytes || entry.DataStream is null)
                throw new InvalidDataException("Signature-Base files exceed the configured limits.");
            var content = new byte[checked((int)entry.Length)];
            await entry.DataStream.ReadExactlyAsync(content, cancellation);
            var digest = SHA256.HashData(content);
            if (path == "LICENSE")
            {
                hasLicense = Convert.ToHexStringLower(digest) == LicenseHash;
                if (!hasLicense) throw new SignatureLicenseException();
            }
            if (path == "README.md")
            {
                hasReadme = Convert.ToHexStringLower(digest) == ReadmeHash;
                if (!hasReadme) throw new SignatureLicenseException();
            }
            await accept(path, kind, content, digest, cancellation);
        }, token);
        if (!hasLicense || !hasReadme) throw new SignatureLicenseException();
        if (!hasIoc || !hasYara)
            throw new InvalidDataException("Signature-Base source or license evidence is incomplete.");
        return count;
    }
}

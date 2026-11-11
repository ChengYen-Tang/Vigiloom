using NuGet.Versioning;

namespace Vigiloom.UpdateSecurity;

internal static class PackageRules
{
    internal const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsRuntime(string? value) => value is "win-x64" or "win-x86" or "linux-x64";
    internal static bool TryVersion(string? value, out NuGetVersion version)
    {
        version = null!;
        return value is { Length: > 0 and <= 128 } && NuGetVersion.TryParse(value, out version!) &&
            !version.HasMetadata && value == version.ToNormalizedString();
    }
}

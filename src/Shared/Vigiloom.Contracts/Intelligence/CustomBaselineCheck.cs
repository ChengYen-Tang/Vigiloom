using System.Globalization;

namespace Vigiloom.Contracts.Intelligence;

/// <summary>The limited read-only operations supported by administrator-reviewed checks.</summary>
public static class CustomBaselineCheck
{
    public const string Prefix = "custom-";

    public static bool IsValid(BaselineCheck? check)
    {
        if (check is null || string.IsNullOrEmpty(check.Id) || !check.Id.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(check.Id.AsSpan(Prefix.Length), "D", out var id) || id.Version != 7 ||
            string.IsNullOrWhiteSpace(check.Title) || check.Title.Length > 256 || check.Title.Any(char.IsControl) ||
            check.Versions is not { Length: > 0 and <= 3 } || check.Versions.Distinct(StringComparer.Ordinal).Count() != check.Versions.Length ||
            check.SourceUrl is null || check.SourceUrl.Length > 2048 || check.SourceUrl.Length > 0 &&
                (!Uri.TryCreate(check.SourceUrl, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https")) ||
            string.IsNullOrWhiteSpace(check.License) || check.License.Length > 256 || check.License.Any(char.IsControl) ||
            check.Guidance is { } guidance && (guidance.Length > 1024 || guidance.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) ||
            string.IsNullOrWhiteSpace(check.Target) || check.Target.Length > 1024 || check.Target.Any(char.IsControl)) return false;
        if (check is { Platform: "linux", Operation: BaselineSources.LinuxShadowOperation })
            return check.Target.StartsWith('/') && check.Target.Length > 1 && check.Versions.All(x => x is "ubuntu:22.04" or "ubuntu:24.04" or "debian:12") &&
                TryMode(check.Expected, out _);
        if (check is { Platform: "windows", Operation: BaselineSources.WindowsRegistryOperation })
            return check.Target.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) &&
                check.Target.LastIndexOf('\\') > 5 && check.Target[^1] != '\\' &&
                check.Versions.All(x => x == "server:2022:member") &&
                uint.TryParse(check.Expected, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        return false;
    }

    public static bool TryMode(string? text, out UnixFileMode mode)
    {
        mode = default;
        if (text is not { Length: 4 } || text[0] != '0' || text.AsSpan(1).ContainsAnyExceptInRange('0', '7')) return false;
        mode = (UnixFileMode)((text[1] - '0') * 64 + (text[2] - '0') * 8 + text[3] - '0');
        return true;
    }
}

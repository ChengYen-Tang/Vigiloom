using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent.Integrity;

public enum IntegrityScopeKind { Directory = 1, Registry = 2 }
public enum IntegrityRegistryView { Registry32 = 1, Registry64 = 2 }

/// <summary>Administrator-supplied policy; identity and revision are assigned by the platform.</summary>
public sealed record IntegrityScopeInput(
    [property: JsonRequired] IntegrityScopeKind Kind,
    [property: JsonRequired] string Root,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] ImmutableArray<string> Exclusions,
    [property: JsonRequired] int ScanIntervalSeconds,
    [property: JsonRequired] int MaximumEntries,
    [property: JsonRequired] long MaximumFileBytes,
    [property: JsonRequired] long MaximumScanBytes,
    [property: JsonRequired] int MaximumDepth,
    IntegrityRegistryView? RegistryView = null)
{
    public IntegrityScope WithIdentity(Guid id, long revision) => new(id, revision, Kind, Root, Enabled, Exclusions,
        ScanIntervalSeconds, MaximumEntries, MaximumFileBytes, MaximumScanBytes, MaximumDepth, RegistryView);
}

/// <summary>A monitoring scope and the version required for changes.</summary>
public sealed record IntegrityScopeView(IntegrityScope Scope, string Version);
/// <summary>A replacement policy and the version last read by the administrator.</summary>
public sealed record IntegrityScopeUpdate([property: JsonRequired] IntegrityScopeInput Policy, [property: JsonRequired] string Version);
/// <summary>The version last read before retiring a monitoring scope.</summary>
public sealed record IntegrityScopeRetire([property: JsonRequired] string Version);

/// <summary>A versioned monitoring boundary. Roots are opt-in and never contain file contents.</summary>
public sealed record IntegrityScope(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] IntegrityScopeKind Kind,
    [property: JsonRequired] string Root,
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] ImmutableArray<string> Exclusions,
    [property: JsonRequired] int ScanIntervalSeconds,
    [property: JsonRequired] int MaximumEntries,
    [property: JsonRequired] long MaximumFileBytes,
    [property: JsonRequired] long MaximumScanBytes,
    [property: JsonRequired] int MaximumDepth,
    IntegrityRegistryView? RegistryView = null)
{
    public const int MaximumScopes = 32;
    public const int MaximumExclusions = 32;

    public bool IsValid() => Id.Version == 7 && Revision > 0 && Enum.IsDefined(Kind) &&
        ValidText(Root, 1024) && ValidRoot() && !Exclusions.IsDefault && Exclusions.Length <= MaximumExclusions &&
        Exclusions.All(ValidExclusion) && ScanIntervalSeconds is >= 60 and <= 604800 &&
        MaximumEntries is >= 1 and <= 20000 && MaximumFileBytes is >= 1 and <= 1024L * 1024 * 1024 &&
        MaximumScanBytes is >= 1 and <= 16L * 1024 * 1024 * 1024 && MaximumFileBytes <= MaximumScanBytes && MaximumDepth is >= 1 and <= 64 &&
        (Kind == IntegrityScopeKind.Registry ? RegistryView.HasValue && Enum.IsDefined(RegistryView.Value) : RegistryView is null);

    public bool SamePolicy(IntegrityScope other) => Kind == other.Kind && Root == other.Root && Enabled == other.Enabled &&
        !Exclusions.IsDefault && !other.Exclusions.IsDefault && Exclusions.SequenceEqual(other.Exclusions, StringComparer.Ordinal) &&
        ScanIntervalSeconds == other.ScanIntervalSeconds && MaximumEntries == other.MaximumEntries &&
        MaximumFileBytes == other.MaximumFileBytes && MaximumScanBytes == other.MaximumScanBytes &&
        MaximumDepth == other.MaximumDepth && RegistryView == other.RegistryView;

    private bool ValidRoot()
    {
        if (Root.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(x => x is "." or "..")) return false;
        if (Kind == IntegrityScopeKind.Registry)
            return !Root.Contains('/') && !Root.Contains("\\\\", StringComparison.Ordinal) && !Root.EndsWith('\\') &&
                (Root.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) || Root.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase));
        return Root.StartsWith('/') && (Root.Length == 1 || Root[1] is not ('/' or '\\')) ||
            Root.Length >= 3 && char.IsAsciiLetter(Root[0]) && Root[1] == ':' && Root[2] is '\\' or '/';
    }

    private static bool ValidExclusion(string? pattern) => ValidText(pattern, 512) && !pattern!.StartsWith('/') &&
        !pattern.Contains('\\') && !pattern.Contains(':') &&
        !pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(x => x is "." or "..");

    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

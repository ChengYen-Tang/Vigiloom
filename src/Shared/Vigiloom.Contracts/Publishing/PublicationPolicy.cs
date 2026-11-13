using System.Net;
using System.Text.RegularExpressions;

namespace Vigiloom.Contracts.Publishing;

/// <summary>The explicit, fixed safety policy available before configurable templates are installed.</summary>
public static partial class PublicationPolicy
{
    public const string BaselineVersion = "baseline-v1";
    public const int MaximumNameLength = 256;

    /// <summary>Requires a confirmed public name without literal IPv4 or IPv6 addresses.</summary>
    public static bool TryValidateName(string? value, out string name)
    {
        name = value?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaximumNameLength || name.Any(char.IsControl)) return false;
        if (IPAddress.TryParse(name, out _)) return false;
        foreach (Match match in IntegerAddressCandidates().Matches(name))
            if (IPAddress.TryParse(match.ValueSpan, out _)) return false;
        foreach (Match match in AddressCharacters().Matches(name))
        {
            var candidate = match.ValueSpan;
            if (candidate.Length >= 7 && IPAddress.TryParse(candidate, out _)) return false;
            if (!candidate.Contains(':') && !candidate.Contains('.')) continue;
            // A name can contain an address without separators (for example, "router10.0.0.1").
            // Names are bounded, so looking at candidate substrings cannot grow with request size.
            for (var start = 0; start < candidate.Length; start++)
                for (var length = 2; length <= candidate.Length - start; length++)
                {
                    var segment = candidate.Slice(start, length);
                    if ((segment.Contains(':') || segment.Contains('.')) && IPAddress.TryParse(segment, out _)) return false;
                }
        }
        return true;
    }

    [GeneratedRegex("[0-9a-fA-FxX:.%]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AddressCharacters();

    [GeneratedRegex("0[xX][0-9a-fA-F]{1,8}|[0-9]{7,10}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IntegerAddressCandidates();
}

/// <summary>Explicit administrative publication change with an optimistic concurrency token.</summary>
public sealed record PublicationChange(bool Public, string? DisplayName, string? PolicyVersion, string? Version);

/// <summary>Current non-secret publication configuration for an administrator.</summary>
public sealed record PublicationSettings(Guid Id, bool Public, string? DisplayName, string? PolicyVersion, string Version);

/// <summary>Preview data for an administrator; it contains no internal addresses.</summary>
public sealed record PublicationPreview(string State, double? LatencyMilliseconds);

public enum PublicationSaveStatus { Updated, Invalid, Conflict, NotFound }
public sealed record PublicationSaveResult(PublicationSaveStatus Status, PublicationSettings? Settings = null);

public interface IPublicationClient
{
    Task<PublicationSettings?> GetAsync(string kind, Guid id, CancellationToken token);
    Task<PublicationPreview?> PreviewAsync(string kind, Guid id, CancellationToken token);
    Task<PublicationSaveResult> SaveAsync(string kind, Guid id, PublicationChange change, CancellationToken token);
}

/// <summary>A bounded anonymous page; only explicitly published resources are represented.</summary>
public sealed record PublicPage<T>(IReadOnlyList<T> Items, int Page, bool HasMore);

/// <summary>Public Ping data without network addresses or host information.</summary>
public sealed record PublicDevice(Guid Id, string Name, string State, double? LatencyMilliseconds, DateTimeOffset? ObservedAt);

/// <summary>A public application's simple health state, independent of any host.</summary>
public sealed record PublicService(Guid Id, string Name, string State);

/// <summary>Public-only group counts, never including private members.</summary>
public sealed record PublicGroup(Guid Id, string Name, int DeviceCount, int ServiceCount);

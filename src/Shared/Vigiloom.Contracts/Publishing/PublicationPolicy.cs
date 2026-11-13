using System.Net;
using System.Text.RegularExpressions;

namespace Vigiloom.Contracts.Publishing;

/// <summary>The explicit, fixed safety policy available before configurable templates are installed.</summary>
public static partial class PublicationPolicy
{
    public const string BaselineVersion = "baseline-v1";
    public const string TemplateVersion = "template-v1";
    public const int MaximumNameLength = 256;
    private const int MaximumAddressChecks = 2048;

    /// <summary>Requires a confirmed public name without literal IPv4 or IPv6 addresses.</summary>
    public static bool TryValidateName(string? value, out string name)
    {
        name = value?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaximumNameLength || name.Any(char.IsControl)) return false;
        if (IPAddress.TryParse(name, out _)) return false;
        foreach (Match match in IntegerAddressCandidates().Matches(name))
            if (IPAddress.TryParse(match.ValueSpan, out _)) return false;
        var checks = 0;
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
                    // An anonymous page may validate many saved names. Bound substring work and
                    // fail closed instead of accepting an address after abandoning the scan.
                    if (++checks > MaximumAddressChecks) return false;
                    var segment = candidate.Slice(start, length);
                    if (!segment.Contains(':') && !segment.Contains('.')) continue;
                    if (IPAddress.TryParse(segment, out _)) return false;
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
public sealed record PublicationChange(bool Public, string? DisplayName, string? PolicyVersion, string? Version, Guid? TemplateId = null, string? TemplateVersion = null);

/// <summary>Current non-secret publication configuration for an administrator.</summary>
public sealed record PublicationSettings(Guid Id, bool Public, string? DisplayName, string? PolicyVersion, string Version, Guid? TemplateId = null, string? TemplateVersion = null);

/// <summary>Preview data for an administrator; it contains no internal addresses.</summary>
public sealed record PublicationPreview(string State, double? LatencyMilliseconds, DateTimeOffset? ObservedAt = null);

public enum PublicationSaveStatus { Updated, Invalid, Conflict, NotFound }
public sealed record PublicationSaveResult(PublicationSaveStatus Status, PublicationSettings? Settings = null);

public interface IPublicationClient
{
    Task<PublicationSettings?> GetAsync(string kind, Guid id, CancellationToken token);
    Task<PublicationPreview?> PreviewAsync(string kind, Guid id, CancellationToken token);
    Task<PublicationTemplatePage> TemplatesAsync(int page, CancellationToken token);
    Task<PublicationTemplateItem?> TemplateAsync(Guid id, CancellationToken token);
    Task<PublicationTemplateItem?> DefaultTemplateAsync(CancellationToken token);
    Task<TemplateOperationResult<PublicationImpact>> TemplateImpactAsync(Guid id, int page, CancellationToken token);
    Task<TemplateOperationResult<PublicationTemplateItem>> CreateTemplateAsync(CreatePublicationTemplate input, CancellationToken token);
    Task<TemplateOperationResult<PublicationTemplateItem>> SetDefaultTemplateAsync(Guid id, SetDefaultPublicationTemplate input, CancellationToken token);
    Task<TemplateOperationResult<PublicationTemplateUpdatePreview>> PreviewTemplateUpdateAsync(Guid id, PublicationTemplateUpdate input, CancellationToken token);
    Task<TemplateOperationResult<PublicationTemplateItem>> ConfirmTemplateUpdateAsync(Guid id, ConfirmPublicationTemplateUpdate input, CancellationToken token);
    Task<TemplateOperationResult<PublicationTemplateRetirementPreview>> PreviewTemplateRetirementAsync(Guid id, PublicationTemplateRetirement input, CancellationToken token);
    Task<TemplateOperationResult<bool>> ConfirmTemplateRetirementAsync(Guid id, ConfirmPublicationTemplateRetirement input, CancellationToken token);
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

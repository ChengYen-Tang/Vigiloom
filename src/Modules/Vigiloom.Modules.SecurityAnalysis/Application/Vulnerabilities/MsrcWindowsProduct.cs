using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Maps only explicitly validated host identities to MSRC product labels.</summary>
internal static class MsrcWindowsProduct
{
    internal static string? ProductName(InventoryEntry? host)
    {
        if (host is null || host.OperatingSystemFamily != "Windows" || host.WindowsEdition is not ("ServerStandard" or "ServerDatacenter") ||
            !string.Equals(host.Architecture, "X64", StringComparison.OrdinalIgnoreCase)) return null;
        var product = (host.WindowsBuild, host.WindowsRelease) switch
        {
            (14393, "1607") => "Windows Server 2016",
            (17763, "1809") => "Windows Server 2019",
            (20348, "21H2") => "Windows Server 2022",
            (26100, "24H2") => "Windows Server 2025",
            _ => null
        };
        if (product is null) return null;
        return host.WindowsInstallationType switch
        {
            "Server" => product,
            "Server Core" => $"{product} (Server Core installation)",
            _ => null
        };
    }

    // Null means ambiguous; false is only absence of this product reference, not a safety conclusion.
    internal static bool? HasProductReference(MsrcCandidate candidate, string productName)
    {
        if (candidate.Document.DuplicateAdvisoryIds.Contains(candidate.Advisory.Cve) || candidate.Advisory.HasUnscopedStatus) return null;
        if (candidate.Advisory.ProductStatuses.Any(x => candidate.Document.AmbiguousProductIds.Contains(x.ProductId)) ||
            candidate.Advisory.Remediations.Any(x => candidate.Document.AmbiguousProductIds.Contains(x.ProductId))) return null;
        var ids = candidate.Document.Products.Where(x => string.Equals(x.Value, productName, StringComparison.Ordinal))
            .Select(x => x.Key).Take(2).ToArray();
        if (ids.Length == 0) return false;
        if (ids.Length != 1 || candidate.Document.AmbiguousProductIds.Contains(ids[0])) return null;
        return candidate.Advisory.ProductStatuses.Any(x => x.ProductId == ids[0]) ||
            candidate.Advisory.Remediations.Any(x => x.ProductId == ids[0]);
    }

    // MSRC JSON Type 3 corresponds to "Known Affected" in the same CVRF XML document.
    internal static bool VendorKnownAffected(MsrcCandidate candidate, string productName) =>
        HasProductReference(candidate, productName) == true && candidate.Advisory.ProductStatuses.Any(status => status.Type == 3 &&
            candidate.Document.Products.TryGetValue(status.ProductId, out var name) && name == productName);

    internal static bool HasOtherProductStatus(MsrcCandidate candidate, string productName) =>
        HasProductReference(candidate, productName) == true && candidate.Advisory.ProductStatuses.Any(status => status.Type != 3 &&
            candidate.Document.Products.TryGetValue(status.ProductId, out var name) && name == productName);

    internal static string[] Severities(MsrcCandidate candidate, string productName) =>
        HasProductReference(candidate, productName) != true ? [] : (candidate.Advisory.Severities ?? []).Where(x =>
            !candidate.Document.AmbiguousProductIds.Contains(x.ProductId) && candidate.Document.Products.TryGetValue(x.ProductId, out var name) && name == productName)
            .Select(x => x.Value).Distinct(StringComparer.Ordinal).ToArray();
}

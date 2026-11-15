namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal readonly record struct MsrcProductCandidate(string ProductName, string Cve);
internal sealed record MsrcProductDiscoveryResult(bool Complete, IReadOnlySet<MsrcProductCandidate> Candidates);

/// <summary>Extracts product-scoped advisory candidates, not host applicability or remediation decisions.</summary>
internal static class MsrcProductDiscovery
{
    internal static MsrcProductDiscoveryResult Discover(MsrcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var candidates = new HashSet<MsrcProductCandidate>();
        var complete = document.HasVulnerabilitySection;
        foreach (var advisory in document.Advisories)
        {
            if (advisory.HasUnscopedStatus || document.DuplicateAdvisoryIds.Contains(advisory.Cve)) complete = false;
            var hasProduct = false;
            foreach (var productId in advisory.ProductStatuses.Select(x => x.ProductId)
                         .Concat(advisory.Remediations.Select(x => x.ProductId)))
            {
                if (document.AmbiguousProductIds.Contains(productId) || !document.Products.TryGetValue(productId, out var product))
                {
                    complete = false;
                    continue;
                }
                hasProduct = true;
                candidates.Add(new(product, advisory.Cve));
            }
            if (!hasProduct) complete = false;
        }
        return new(complete, candidates);
    }
}

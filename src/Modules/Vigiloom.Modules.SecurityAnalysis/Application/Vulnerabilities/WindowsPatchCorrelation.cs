using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record WindowsCveEvidence(string Cve, IReadOnlyList<string> ObservedKbs,
    bool HasCustomerActionNotice, bool RebootObserved, bool AmbiguousProduct, bool VendorKnownAffected = false,
    IReadOnlyList<string>? SupersedingKbs = null, IReadOnlyList<WindowsCatalogReference>? CatalogReferences = null, bool CatalogReferencesTruncated = false,
    string? MicrosoftSeverity = null);
internal sealed record WindowsCatalogEvidence(string Product, string InstalledKb, string UpdateId, DateTimeOffset ObservedAt,
    IReadOnlyList<string> SupersededKbs);

/// <summary>Correlates positive installed-KB observations; missing observations are not negative evidence.</summary>
internal static class WindowsPatchCorrelation
{
    // Follow the vendor-remediation/installed-hotfix correlation used by Wazuh.
    internal static MatchState Classify(WindowsCveEvidence evidence)
    {
        if (evidence.AmbiguousProduct || evidence.RebootObserved) return MatchState.Unknown;
        if (evidence.ObservedKbs.Count > 0 || evidence.SupersedingKbs is { Count: > 0 }) return MatchState.Fixed;
        return evidence.VendorKnownAffected ? MatchState.Affected : MatchState.Unknown;
    }

    internal static IReadOnlyList<WindowsCveEvidence> Correlate(WindowsProductAssessment assessment, IReadOnlyList<WindowsCatalogEvidence>? catalog = null, DateTimeOffset? now = null, CancellationToken token = default)
    {
        if (assessment.Status != WindowsEvaluationStatus.Ready || assessment.Candidates is null)
            throw new ArgumentException("A completed product assessment is required.", nameof(assessment));
        if (catalog is not null && now is null) throw new ArgumentException("Catalog observations require a reference time.", nameof(now));
        var installed = InstalledKbs(assessment);
        var replacements = new Dictionary<string, HashSet<WindowsCatalogReference>>(StringComparer.Ordinal);
        var totalRelations = 0;
        foreach (var item in catalog ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (item.Product != assessment.Product || item.ObservedAt > now!.Value || item.ObservedAt < now.Value.AddDays(-1) ||
                !Guid.TryParse(item.UpdateId, out var updateId) || updateId == Guid.Empty ||
                NormalizeKb(item.InstalledKb) is not { } installedKb || !installed.ContainsKey(installedKb)) continue;
            if (item.SupersededKbs.Count > 50_000 - totalRelations) throw new InvalidDataException("Catalog relations exceed the assessment limit.");
            totalRelations += item.SupersededKbs.Count;
            foreach (var value in item.SupersededKbs)
            {
                token.ThrowIfCancellationRequested();
                if (NormalizeKb(value) is { } superseded)
                {
                    if (!replacements.TryGetValue(superseded, out var available)) replacements.Add(superseded, available = []);
                    available.Add(new(installedKb, superseded, updateId, item.ObservedAt));
                }
            }
        }
        return assessment.Candidates.GroupBy(x => x.Cve, StringComparer.Ordinal).Select(group =>
        {
            token.ThrowIfCancellationRequested();
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var superseding = new HashSet<string>(StringComparer.Ordinal);
            var references = new HashSet<WindowsCatalogReference>();
            var reboot = false;
            foreach (var candidate in group)
                foreach (var fix in candidate.Remediations)
                    if (NormalizeKb(fix.Description) is { } kb)
                    {
                        if (installed.TryGetValue(kb, out var pending)) { observed.Add(kb); reboot |= pending; }
                        if (replacements.TryGetValue(kb, out var newer))
                            foreach (var reference in newer) { references.Add(reference); superseding.Add(reference.InstalledKb); reboot |= installed[reference.InstalledKb]; }
                    }
            var knownAffected = group.Any(x => x.VendorKnownAffected);
            var orderedReferences = references.OrderBy(x => x.InstalledKb, StringComparer.Ordinal)
                .ThenBy(x => x.SupersededKb, StringComparer.Ordinal).ThenBy(x => x.UpdateId).ThenBy(x => x.ObservedAt).Take(40).ToArray();
            var severity = group.SelectMany(x => x.Severities ?? []).OrderBy(SeverityRank).FirstOrDefault();
            var evidence = new WindowsCveEvidence(group.Key, observed.Order(StringComparer.Ordinal).ToArray(),
                group.Any(x => x.HasCustomerActionNotice), reboot,
                group.Any(x => x.ProductReference is null) || knownAffected && group.Any(x => x.OtherProductStatus), knownAffected,
                superseding.Order(StringComparer.Ordinal).ToArray(), orderedReferences, references.Count > orderedReferences.Length, severity);
            while (evidence.CatalogReferences is { Count: > 0 } included && System.Text.Json.JsonSerializer.Serialize(evidence).Length > 8192)
                evidence = evidence with { CatalogReferences = included.Take(included.Count / 2).ToArray(), CatalogReferencesTruncated = true };
            return evidence;
        }).ToArray();
    }

    private static int SeverityRank(string severity) => severity switch { "Critical" => 0, "Important" => 1, "Moderate" => 2, "Low" => 3, _ => 4 };

    internal static Dictionary<string, bool> InstalledKbs(WindowsProductAssessment assessment)
    {
        var installed = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (assessment.QuickFix is { } quickFix)
            foreach (var patch in quickFix.Items)
                if (NormalizeKb(patch.Key) is { } kb) installed.TryAdd(kb, false);
        if (assessment.Updates is { } updates)
            foreach (var update in updates.Items)
                if (update.UpdateInstalled == true)
                    foreach (var value in update.UpdateKbs ?? [])
                        if (NormalizeKb(value) is { } kb)
                            installed[kb] = installed.GetValueOrDefault(kb) || update.UpdateRebootRequired == true;
        return installed;
    }

    private static string? NormalizeKb(string? value)
    {
        if (value is null) return null;
        var digits = value.AsSpan();
        if (digits.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) digits = digits[2..];
        return digits.Length is >= 6 and <= 10 && digits.IndexOfAnyExceptInRange('0', '9') < 0 ? digits.ToString() : null;
    }
}

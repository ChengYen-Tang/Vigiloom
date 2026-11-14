namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

/// <summary>Classifies source license evidence without authorizing rule execution or distribution.</summary>
internal static class SigmaLicense
{
    internal const string RepositoryDrl = "repository-drl-1.1";
    internal const string DeclaredDrl = "declared-drl-1.1";
    internal const string NeedsReview = "needs-review";

    internal static string Classify(string? declaredLicense) => declaredLicense switch
    {
        null => RepositoryDrl,
        "DRL-1.1" => DeclaredDrl,
        _ => NeedsReview
    };

    internal static string? ExclusionReason(string path, string? author, string? license)
    {
        if (!path.StartsWith("rules/", StringComparison.Ordinal)) return "outside-core-rules";
        if (string.IsNullOrWhiteSpace(author) || author.Trim() == "~" || author.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)) return "missing-author";
        return Classify(license) == NeedsReview ? "unreviewed-license" : null;
    }
}

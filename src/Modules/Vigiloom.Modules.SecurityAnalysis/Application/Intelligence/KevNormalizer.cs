using System.Globalization;
using System.Text.Json;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Maps a previously validated catalog without losing the retained original artifact.</summary>
internal static class KevNormalizer
{
    internal static IEnumerable<KevRecord> Read(ReadOnlyMemory<byte> content, Guid revisionId)
    {
        using var document = JsonDocument.Parse(content);
        foreach (var item in document.RootElement.GetProperty("vulnerabilities").EnumerateArray())
        {
            yield return new KevRecord
            {
                RevisionId = revisionId,
                CveId = item.GetProperty("cveID").GetString()!,
                VendorProject = item.GetProperty("vendorProject").GetString()!,
                Product = item.GetProperty("product").GetString()!,
                VulnerabilityName = item.GetProperty("vulnerabilityName").GetString()!,
                DateAdded = DateOnly.ParseExact(item.GetProperty("dateAdded").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Description = item.GetProperty("shortDescription").GetString()!,
                RequiredAction = item.GetProperty("requiredAction").GetString()!,
                DueDate = DateOnly.ParseExact(item.GetProperty("dueDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                RansomwareUse = OptionalString(item, "knownRansomwareCampaignUse"),
                ForensicTriage = OptionalString(item, "forensicTriage"),
                Notes = OptionalString(item, "notes"),
                CwesJson = item.TryGetProperty("cwes", out var cwes) ? cwes.GetRawText() : null
            };
        }
    }

    private static string? OptionalString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() : null;
}

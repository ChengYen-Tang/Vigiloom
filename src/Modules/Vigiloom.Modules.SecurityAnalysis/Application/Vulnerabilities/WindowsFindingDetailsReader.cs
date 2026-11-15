using System.Text.Json;
using Vigiloom.Contracts.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal static class WindowsFindingDetailsReader
{
    internal static WindowsFindingDetails? Read(string? json)
    {
        if (json is null || json.Length > 8192) return null;
        try
        {
            var result = JsonSerializer.Deserialize<WindowsFindingDetails>(json);
            return result?.ObservedKbs is { Count: <= 500 } ids && ids.All(ValidKb) &&
                (result.SupersedingKbs is null || result.SupersedingKbs.Count <= 500 && result.SupersedingKbs.All(ValidKb))
                && (result.CatalogReferences is null || result.CatalogReferences.Count <= 40 && result.CatalogReferences.All(x =>
                    x is not null && ValidKb(x.InstalledKb) && ValidKb(x.SupersededKb) && x.UpdateId != Guid.Empty))
                && result.MicrosoftSeverity is null or "Critical" or "Important" or "Moderate" or "Low"
                ? result : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidKb(string value) => value is { Length: >= 6 and <= 10 } && value.All(char.IsAsciiDigit);
}

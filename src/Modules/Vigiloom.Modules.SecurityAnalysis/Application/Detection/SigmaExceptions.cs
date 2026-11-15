using Vigiloom.Contracts.Intelligence;
using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Applies local exact-value exclusions only to positively matched, decoded events.</summary>
internal static class SigmaExceptions
{
    internal const int MaximumCount = 20;

    internal static bool Valid(IReadOnlyList<SigmaRuleException>? items) => items is null ||
        items.Count <= MaximumCount && items.All(x => x is not null && !string.IsNullOrWhiteSpace(x.Field) &&
            x.Field.Length <= 128 && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 1024);

    internal static bool TryRead(string evidence, out IReadOnlyList<SigmaRuleException> exceptions)
    {
        exceptions = [];
        try
        {
            using var document = JsonDocument.Parse(evidence);
            if (document.RootElement.ValueKind == JsonValueKind.Array) return true;
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("Exceptions", out var value)) return true;
            var parsed = value.Deserialize<SigmaRuleException[]>();
            if (parsed is null || !Valid(parsed)) return false;
            exceptions = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static SigmaMatch Apply(SigmaMatch match, HostEvent log, IReadOnlyList<SigmaRuleException>? items)
    {
        if (match != SigmaMatch.Yes || log.Status != HostEventStatus.Ready || items is null) return match;
        foreach (var item in items)
            if (log.Fields.TryGetValue(item.Field, out var values) &&
                values.Any(value => string.Equals(value, item.Value, StringComparison.OrdinalIgnoreCase)))
                return SigmaMatch.No;
        return match;
    }
}

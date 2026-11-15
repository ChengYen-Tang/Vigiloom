using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal static class CorrelationTestEvidence
{
    internal static bool Matches(string json, SigmaCorrelationEntry correlation)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("BasePath", out var path) && path.GetString() == correlation.Plan.BaseRule.Source.Path &&
                root.TryGetProperty("BaseSha256", out var digest) && digest.GetString() == correlation.Plan.BaseRule.Source.Sha256 &&
                root.TryGetProperty("MaximumLatenessSeconds", out var late) && late.GetInt32() == (int)CorrelationEventStore.MaximumLateness.TotalSeconds &&
                root.TryGetProperty("MaximumFutureSkewSeconds", out var skew) && skew.GetInt32() == (int)CorrelationEventStore.MaximumFutureSkew.TotalSeconds;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }
}

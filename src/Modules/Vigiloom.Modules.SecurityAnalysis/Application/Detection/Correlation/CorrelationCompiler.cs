using System.Globalization;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

internal sealed record EventCountRule(string Reference, IReadOnlyList<string> GroupBy, TimeSpan Window, int MinimumCount);
internal sealed record CorrelationCompileResult(EventCountRule? Rule, string? Error);

/// <summary>Compiles the supported single-reference event-count subset of Sigma correlations.</summary>
internal static class CorrelationCompiler
{
    internal static CorrelationCompileResult Compile(ReadOnlySpan<byte> content, TimeSpan maximumWindow)
    {
        try
        {
            var metadata = SigmaRuleValidator.Validate(content);
            if (!metadata.IsCorrelation) throw new InvalidDataException("A Sigma correlation rule is required.");
            if (maximumWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumWindow));
            var yaml = new YamlStream();
            yaml.Load(new StringReader(new UTF8Encoding(false, true).GetString(content)));
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            if (root.Children.ContainsKey(new YamlScalarNode("taxonomy"))) throw new InvalidDataException("Custom Sigma taxonomy is not supported.");
            if (root.Children.ContainsKey(new YamlScalarNode("generate"))) throw new InvalidDataException("Sigma generate behavior is not supported.");
            if (Get(root, "correlation") is not YamlMappingNode correlation) throw new InvalidDataException("Invalid correlation section.");
            var allowed = new HashSet<string>(["type", "rules", "group-by", "timespan", "condition"], StringComparer.Ordinal);
            if (correlation.Children.Keys.Any(x => x is not YamlScalarNode key || key.Value is null || !allowed.Contains(key.Value)))
                throw new InvalidDataException("Unsupported Sigma correlation option.");
            if (Scalar(Get(correlation, "type")) != "event_count") throw new InvalidDataException("Unsupported Sigma correlation type.");
            if (Get(correlation, "rules") is not YamlSequenceNode { Children.Count: 1 } references)
                throw new InvalidDataException("Exactly one referenced rule is supported.");
            var reference = Scalar(references.Children[0]);
            if (reference.Length > 256) throw new InvalidDataException("Invalid rule reference.");
            if (Get(correlation, "group-by") is not YamlSequenceNode groups || groups.Children.Count is < 1 or > 8)
                throw new InvalidDataException("One to eight group fields are required.");
            var fields = groups.Children.Select(Scalar).ToArray();
            if (fields.Any(x => x.Length > 256) || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
                throw new InvalidDataException("Invalid correlation group fields.");
            var span = Scalar(Get(correlation, "timespan"));
            if (span.Length < 2 || !long.TryParse(span.AsSpan(0, span.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount < 1)
                throw new InvalidDataException("Invalid correlation timespan.");
            var secondsPerUnit = span[^1] switch { 's' => 1L, 'm' => 60L, 'h' => 3600L, 'd' => 86400L, _ => 0L };
            if (secondsPerUnit == 0 || amount > maximumWindow.TotalSeconds / secondsPerUnit)
                throw new InvalidDataException("Correlation timespan exceeds the supported limit.");
            var window = TimeSpan.FromSeconds(amount * secondsPerUnit);
            if (Get(correlation, "condition") is not YamlMappingNode condition || condition.Children.Count != 1 ||
                !condition.Children.TryGetValue(new YamlScalarNode("gte"), out var threshold) ||
                !int.TryParse(Scalar(threshold), NumberStyles.None, CultureInfo.InvariantCulture, out var minimum) || minimum < 1)
                throw new InvalidDataException("Only a positive gte event-count threshold is supported.");
            return new(new(reference, fields, window, minimum), null);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or YamlException or DecoderFallbackException)
        {
            return new(null, error.Message);
        }
    }

    private static YamlNode Get(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : throw new InvalidDataException("Missing correlation field.");

    private static string Scalar(YamlNode node) => node is YamlScalarNode { Value: { Length: > 0 } value } scalar &&
        !string.IsNullOrWhiteSpace(value) && !(scalar.Style == ScalarStyle.Plain && (value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)))
        ? value : throw new InvalidDataException("Invalid correlation value.");
}

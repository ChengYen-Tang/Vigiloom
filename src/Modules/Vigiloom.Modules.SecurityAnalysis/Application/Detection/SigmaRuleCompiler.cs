using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal sealed record CompiledSigmaRule(SigmaRuleMetadata Metadata, Func<HostEvent, SigmaMatch> Predicate)
{
    internal SigmaMatch Match(HostEvent log) => SigmaLogSource.Evaluate(Metadata, log) switch
    {
        SigmaMatch.Yes => Predicate(log),
        SigmaMatch.No => SigmaMatch.No,
        _ => SigmaMatch.Unknown
    };
}
internal sealed record SigmaCompileResult(CompiledSigmaRule? Rule, string? Error);

/// <summary>Compiles the supported Sigma subset; unsupported rules return an explicit diagnostic.</summary>
internal static class SigmaRuleCompiler
{
    internal static SigmaCompileResult Compile(ReadOnlySpan<byte> content, SigmaRuleMetadata? validatedMetadata = null)
    {
        try
        {
            var metadata = validatedMetadata ?? SigmaRuleValidator.Validate(content);
            if (!SigmaLogSource.IsSupported(metadata)) throw new InvalidDataException("Unsupported Sigma log source.");
            var yaml = new YamlStream();
            yaml.Load(new StringReader(new UTF8Encoding(false, true).GetString(content)));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
                !root.Children.TryGetValue(new YamlScalarNode("detection"), out var detectionNode) || detectionNode is not YamlMappingNode detection)
                throw new InvalidDataException("A single Sigma detection mapping is required.");
            var selections = new Dictionary<string, SigmaFieldCondition[][]>(StringComparer.Ordinal);
            string? condition = null;
            foreach (var (keyNode, valueNode) in detection.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key }) throw new InvalidDataException("Invalid Sigma selection name.");
                if (key == "condition")
                {
                    condition = Scalar(valueNode);
                    continue;
                }
                if (valueNode is YamlSequenceNode keywordList && keywordList.Children.All(x => x is YamlScalarNode))
                {
                    var keywords = keywordList.Children.Select(x => new SigmaPattern(Scalar(x), "contains")).ToArray();
                    selections.Add(key, [[SigmaFieldCondition.Keywords(keywords)]]);
                    continue;
                }
                var maps = valueNode switch
                {
                    YamlMappingNode map => new[] { map },
                    YamlSequenceNode sequence when sequence.Children.All(x => x is YamlMappingNode) => sequence.Children.Cast<YamlMappingNode>().ToArray(),
                    _ => throw new InvalidDataException("Only field-mapping selections are supported.")
                };
                if (maps.Length == 0) throw new InvalidDataException("Sigma selection is empty.");
                selections.Add(key, maps.Select(CompileMap).ToArray());
            }
            if (string.IsNullOrWhiteSpace(condition)) throw new InvalidDataException("Sigma condition is missing.");
            return new(new(metadata, SigmaCondition.Compile(condition, selections)), null);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or YamlException or DecoderFallbackException)
        {
            return new(null, error.Message);
        }
    }

    private static SigmaFieldCondition[] CompileMap(YamlMappingNode mapping)
    {
        if (mapping.Children.Count == 0) throw new InvalidDataException("Sigma selection is empty.");
        if (mapping.Children.Count == 1 && mapping.Children.TryGetValue(new YamlScalarNode("|all"), out var keywordNode))
        {
            if (keywordNode is not YamlSequenceNode keywordList) throw new InvalidDataException("Sigma keyword list is invalid.");
            return [SigmaFieldCondition.Keywords(keywordList.Children.Select(x => new SigmaPattern(Scalar(x), "contains")).ToArray(), all: true)];
        }
        var fields = new List<SigmaFieldCondition>();
        foreach (var (fieldNode, valuesNode) in mapping.Children)
        {
            var parts = Scalar(fieldNode).Split('|');
            if (string.IsNullOrWhiteSpace(parts[0])) throw new InvalidDataException("Sigma field name is empty.");
            string? modifier = null;
            var all = false;
            var exists = false;
            foreach (var part in parts.Skip(1))
            {
                if (part == "all" && !all) all = true;
                else if (part == "exists" && !exists) exists = true;
                else if (part is "contains" or "startswith" or "endswith" && modifier is null) modifier = part;
                else throw new InvalidDataException("Unsupported Sigma modifier.");
            }
            if (exists)
            {
                if (all || modifier is not null || !bool.TryParse(Scalar(valuesNode), out var required))
                    throw new InvalidDataException("Invalid Sigma exists test.");
                fields.Add(new(parts[0], [], exists: required));
                continue;
            }
            var values = valuesNode is YamlSequenceNode sequence
                ? sequence.Children.Select(Scalar).ToArray() : [Scalar(valuesNode)];
            if (values.Length == 0) throw new InvalidDataException("Sigma field values are empty.");
            fields.Add(new(parts[0], values.Select(value => new SigmaPattern(value, modifier)).ToArray(), all));
        }
        return fields.ToArray();
    }

    private static string Scalar(YamlNode node)
    {
        if (node is not YamlScalarNode { Value: { } value } scalar ||
            scalar.Style == ScalarStyle.Plain && (value.Length == 0 || value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only non-null scalar Sigma values are supported.");
        return value;
    }
}

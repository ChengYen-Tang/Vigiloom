using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;

internal sealed record SigmaRuleMetadata(string Title, string? Id, string? Author, string? Status, string? License,
    string? Product, string? Category, string? Service);

/// <summary>Reads bounded rule metadata without deserializing arbitrary YAML objects or evaluating detections.</summary>
internal static class SigmaRuleValidator
{
    internal const int MaximumRuleBytes = 1024 * 1024;
    private const int MaximumDepth = 64;
    private const int MaximumEvents = 100_000;

    internal static SigmaRuleMetadata Validate(ReadOnlySpan<byte> content)
    {
        if (content.Length is < 1 or > MaximumRuleBytes) throw new InvalidDataException("Invalid Sigma rule size.");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(content);
            var parser = new Parser(new StringReader(text));
            var frames = new Stack<Frame>();
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var documents = 0;
            var count = 0;
            var rootSeen = false;
            var logSource = false;
            var detection = false;
            while (parser.MoveNext())
            {
                if (++count > MaximumEvents) throw new InvalidDataException("Sigma rule has too many YAML events.");
                var current = parser.Current;
                if (current is AnchorAlias || current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                    throw new InvalidDataException("YAML anchors, aliases and explicit tags are not supported.");
                switch (current)
                {
                    case DocumentStart:
                        if (++documents != 1) throw new InvalidDataException("Sigma rule must contain one YAML document.");
                        break;
                    case MappingStart or SequenceStart:
                        string? section = null;
                        if (frames.Count == 0)
                        {
                            if (rootSeen || current is not MappingStart) throw new InvalidDataException("Sigma rule root must be a mapping.");
                            rootSeen = true;
                        }
                        else
                        {
                            var parent = frames.Peek();
                            if (parent.Mapping)
                            {
                                if (parent.ExpectingKey) throw new InvalidDataException("Complex YAML mapping keys are not supported.");
                                if (frames.Count == 1)
                                {
                                    section = parent.Key;
                                    if (parent.Key == "logsource") logSource = current is MappingStart;
                                    if (parent.Key == "detection") detection = current is MappingStart;
                                    if (IsMetadata(parent.Key)) throw new InvalidDataException("Sigma metadata must be scalar.");
                                }
                                else if (frames.Count == 2 && parent.Section == "logsource" && IsLogSource(parent.Key))
                                    throw new InvalidDataException("Sigma log source fields must be scalar.");
                                parent.ExpectingKey = true;
                            }
                        }
                        frames.Push(new Frame(current is MappingStart, section));
                        if (frames.Count > MaximumDepth) throw new InvalidDataException("Sigma YAML nesting exceeds the limit.");
                        break;
                    case MappingEnd or SequenceEnd:
                        if (frames.Count == 0) throw new InvalidDataException("Invalid YAML structure.");
                        var frame = frames.Pop();
                        if (frame.Mapping && !frame.ExpectingKey) throw new InvalidDataException("Incomplete YAML mapping.");
                        break;
                    case Scalar scalar:
                        if (frames.Count == 0) throw new InvalidDataException("Sigma rule root must be a mapping.");
                        var top = frames.Peek();
                        if (!top.Mapping) break;
                        if (top.ExpectingKey)
                        {
                            if (string.IsNullOrEmpty(scalar.Value) || !top.Keys.Add(scalar.Value)) throw new InvalidDataException("Invalid or duplicate YAML key.");
                            top.Key = scalar.Value;
                            top.ExpectingKey = false;
                        }
                        else
                        {
                            if (frames.Count == 1 && IsMetadata(top.Key)) metadata.Add(top.Key!, scalar.Value);
                            if (frames.Count == 2 && top.Section == "logsource" && IsLogSource(top.Key)) metadata.Add("logsource." + top.Key, scalar.Value);
                            top.ExpectingKey = true;
                        }
                        break;
                }
            }
            if (documents != 1 || !rootSeen || frames.Count != 0 || !logSource || !detection ||
                !metadata.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title) || title.Length > 256)
                throw new InvalidDataException("Sigma rule is missing required fields.");
            if (metadata.TryGetValue("license", out var declaredLicense) && (string.IsNullOrWhiteSpace(declaredLicense) ||
                declaredLicense == "~" || declaredLicense.Equals("null", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Sigma rule has an unresolved license declaration.");
            return new(title, Get("id"), Get("author"), Get("status"), Get("license"), Get("logsource.product"), Get("logsource.category"), Get("logsource.service"));

            string? Get(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch (Exception error) when (error is YamlException or DecoderFallbackException)
        {
            throw new InvalidDataException("Invalid Sigma rule encoding or YAML.", error);
        }
    }

    private static bool IsMetadata(string? key) => key is "title" or "id" or "author" or "status" or "license";
    private static bool IsLogSource(string? key) => key is "product" or "category" or "service";

    private sealed class Frame(bool mapping, string? section)
    {
        internal bool Mapping { get; } = mapping;
        internal string? Section { get; } = section;
        internal bool ExpectingKey { get; set; } = true;
        internal string? Key { get; set; }
        internal HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }
}

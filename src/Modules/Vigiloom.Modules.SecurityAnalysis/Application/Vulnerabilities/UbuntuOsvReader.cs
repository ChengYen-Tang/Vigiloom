using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record UbuntuOsvRecord(string Id, DateTimeOffset ModifiedAt, bool Withdrawn, IReadOnlyList<UbuntuOsvAdvisory> Packages,
    DateTimeOffset? WithdrawnAt = null, string? UbuntuPriority = null);

/// <summary>Reads one bounded Canonical CVE OSV document; unknown range/event kinds remain unsupported.</summary>
internal static partial class UbuntuOsvReader
{
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemanticVersion();

    internal static UbuntuOsvRecord Read(ReadOnlyMemory<byte> data, int maximumBytes)
    {
        if (maximumBytes <= 0 || data.Length == 0 || data.Length > maximumBytes) throw new InvalidDataException("OSV document exceeds its bounds.");
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.TryGetProperty("schema_version", out var schemaValue))
            {
                if (schemaValue.ValueKind != JsonValueKind.String || schemaValue.GetString() is not { Length: > 0 and <= 128 } schema ||
                    !SemanticVersion().IsMatch(schema) || !schema.StartsWith("1.", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported OSV schema version.");
            }
            var id = Required(root, "id", 128);
            if (!id.StartsWith("UBUNTU-CVE-", StringComparison.Ordinal)) throw new InvalidDataException("Not a Canonical Ubuntu CVE record.");
            if (!TryTimestamp(root.GetProperty("modified"), out var modified)) throw new InvalidDataException("OSV modified timestamp is invalid.");
            var withdrawn = root.TryGetProperty("withdrawn", out var withdrawnValue);
            DateTimeOffset? withdrawnAt = null;
            if (withdrawn)
            {
                if (!TryTimestamp(withdrawnValue, out var withdrawnTime)) throw new InvalidDataException("OSV withdrawn timestamp is invalid.");
                withdrawnAt = withdrawnTime.ToUniversalTime();
            }
            var packages = new List<UbuntuOsvAdvisory>();
            if (root.TryGetProperty("affected", out var affected))
            {
                if (affected.ValueKind != JsonValueKind.Array || affected.GetArrayLength() > 5000) throw new InvalidDataException("Invalid OSV affected list.");
                foreach (var item in affected.EnumerateArray())
                {
                    var package = item.GetProperty("package");
                    var ecosystem = Required(package, "ecosystem", 256);
                    var name = Required(package, "name", 256);
                    var ranges = new List<UbuntuOsvRangeEvidence>();
                    if (item.TryGetProperty("ranges", out var rangeArray))
                    {
                        if (rangeArray.ValueKind != JsonValueKind.Array || rangeArray.GetArrayLength() > 128) throw new InvalidDataException("Invalid OSV ranges.");
                        foreach (var range in rangeArray.EnumerateArray())
                        {
                            var type = Required(range, "type", 64);
                            var eventArray = range.GetProperty("events");
                            if (eventArray.ValueKind != JsonValueKind.Array || eventArray.GetArrayLength() > 256) throw new InvalidDataException("Invalid OSV events.");
                            var events = new List<OsvVersionEvent>(eventArray.GetArrayLength());
                            foreach (var entry in eventArray.EnumerateArray())
                            {
                                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid OSV event.");
                                using var fields = entry.EnumerateObject();
                                if (!fields.MoveNext()) throw new InvalidDataException("Empty OSV event.");
                                var field = fields.Current;
                                if (fields.MoveNext() || field.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid OSV event fields.");
                                var version = field.Value.GetString();
                                if (string.IsNullOrWhiteSpace(version) || version.Length > 2048) throw new InvalidDataException("Invalid OSV event version.");
                                var kind = field.Name switch { "introduced" => OsvEventKind.Introduced, "fixed" => OsvEventKind.Fixed,
                                    "last_affected" => OsvEventKind.LastAffected, _ => OsvEventKind.Limit };
                                events.Add(new(kind, version));
                            }
                            ranges.Add(new(type, events));
                        }
                    }
                    var versions = new List<string>();
                    if (item.TryGetProperty("versions", out var versionArray))
                    {
                        if (versionArray.ValueKind != JsonValueKind.Array || versionArray.GetArrayLength() > 100_000) throw new InvalidDataException("Invalid OSV versions.");
                        foreach (var value in versionArray.EnumerateArray())
                        {
                            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 2048 } version)
                                throw new InvalidDataException("Invalid OSV version.");
                            versions.Add(version);
                        }
                    }
                    // Parsing does not establish that the selected source revision is current or complete.
                    packages.Add(new(ecosystem, name, withdrawn, false, ranges, versions));
                }
            }
            return new(id, modified.ToUniversalTime(), withdrawn, packages, withdrawnAt, ReadUbuntuPriority(root));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Invalid Canonical OSV document.", error);
        }
    }

    private static string? ReadUbuntuPriority(JsonElement root)
    {
        if (!root.TryGetProperty("severity", out var severity) || severity.ValueKind != JsonValueKind.Array || severity.GetArrayLength() > 128) return null;
        string? priority = null;
        foreach (var entry in severity.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Ubuntu") continue;
            if (!entry.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.String) return null;
            var value = score.GetString();
            if (value is not ("critical" or "high" or "medium" or "low" or "negligible") || priority is not null && priority != value) return null;
            priority = value;
        }
        return priority;
    }

    private static string Required(JsonElement element, string property, int maximumLength)
    {
        var value = element.GetProperty(property).GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength ? value : throw new InvalidDataException("Invalid OSV field.");
    }

    private static bool TryTimestamp(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String) return false;
        var text = element.GetString();
        return text is not null && (text.EndsWith('Z') || text.Length >= 6 &&
            (text[^6] == '+' || text[^6] == '-') && text[^3] == ':') && element.TryGetDateTimeOffset(out value);
    }
}

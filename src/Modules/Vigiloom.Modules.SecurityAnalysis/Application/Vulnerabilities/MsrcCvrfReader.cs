using System.Text.Json;
using System.Globalization;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record MsrcRemediation(string ProductId, string? Description, string? FixedBuild, string? Supersedence, string? Subtype);
internal sealed record MsrcProductStatus(string ProductId, int Type);
internal sealed record MsrcSeverity(string ProductId, string Value);
internal sealed record MsrcAdvisory(string Cve, bool HasUnscopedStatus, IReadOnlyList<MsrcProductStatus> ProductStatuses, IReadOnlyList<MsrcRemediation> Remediations, bool HasCustomerActionNotice = false,
    IReadOnlyList<MsrcSeverity>? Severities = null);
internal sealed record MsrcDocument(string Id, DateTimeOffset ModifiedAt, bool HasVulnerabilitySection, IReadOnlyDictionary<string, string> Products, IReadOnlySet<string> AmbiguousProductIds, IReadOnlySet<string> DuplicateAdvisoryIds, IReadOnlyList<MsrcAdvisory> Advisories);

/// <summary>Reads bounded MSRC CVRF vendor-fix evidence; this does not determine local applicability or update installation.</summary>
internal static class MsrcCvrfReader
{
    internal static MsrcDocument Read(ReadOnlyMemory<byte> json, int maximumBytes, int maximumRecords)
    {
        if (maximumBytes <= 0 || maximumRecords <= 0 || json.Length == 0 || json.Length > maximumBytes)
            throw new InvalidDataException("MSRC document exceeds its configured bounds.");
        try
        {
            var reader = new Utf8JsonReader(json.Span, new JsonReaderOptions { MaxDepth = 32 });
            var properties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) properties.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) properties.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !properties.Peek().Add(reader.GetString()!)) throw Invalid();
            }
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var id = RequiredText(root.GetProperty("DocumentTracking").GetProperty("Identification").GetProperty("ID"), "Value", 64);
            var release = RequiredText(root.GetProperty("DocumentTracking"), "CurrentReleaseDate", 64);
            if (!DateTimeOffset.TryParse(release, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified)) throw Invalid();
            var products = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            var tree = root.GetProperty("ProductTree");
            var pending = new Stack<JsonElement>();
            pending.Push(tree.GetProperty("Branch"));
            if (tree.TryGetProperty("FullProductName", out var fullProducts)) pending.Push(fullProducts);
            while (pending.TryPop(out var item))
            {
                if (item.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in item.EnumerateArray()) pending.Push(child);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("ProductID", out _))
                    {
                        var key = RequiredText(item, "ProductID", 128);
                        var name = RequiredText(item, "Value", 512);
                        if (products.TryGetValue(key, out var existing)) { if (existing != name) ambiguous.Add(key); }
                        else products[key] = name;
                        if (products.Count > maximumRecords) throw Invalid();
                    }
                    if (item.TryGetProperty("Items", out var children)) pending.Push(children);
                }
                else throw Invalid();
            }
            var advisories = new List<MsrcAdvisory>();
            var cves = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            var hasVulnerabilities = root.TryGetProperty("Vulnerability", out var vulnerabilityArray);
            if (hasVulnerabilities && vulnerabilityArray.ValueKind != JsonValueKind.Array) throw Invalid();
            foreach (var vulnerability in hasVulnerabilities ? vulnerabilityArray.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var cve = OptionalText(vulnerability, "CVE", 64);
                if (cve is null) continue;
                if (!cves.Add(cve)) duplicates.Add(cve);
                var statuses = new List<MsrcProductStatus>();
                var actionNotice = vulnerability.TryGetProperty("Notes", out var notes) && notes.ValueKind == JsonValueKind.Array &&
                    notes.EnumerateArray().Any(note => note.ValueKind == JsonValueKind.Object &&
                        note.TryGetProperty("Type", out var kind) && kind.ValueKind == JsonValueKind.Number && kind.TryGetInt32(out var number) && number == 6 &&
                        note.TryGetProperty("Title", out var title) && title.ValueKind == JsonValueKind.String && title.GetString() == "Customer Action Required" &&
                        note.TryGetProperty("Value", out var answer) && answer.ValueKind == JsonValueKind.String &&
                        string.Equals(answer.GetString(), "Yes", StringComparison.OrdinalIgnoreCase));
                var unscoped = false;
                if (vulnerability.TryGetProperty("ProductStatuses", out var productStatuses) && productStatuses.ValueKind != JsonValueKind.Null)
                    foreach (var status in productStatuses.EnumerateArray())
                    {
                        var statusType = status.GetProperty("Type").GetInt32();
                        if (!status.TryGetProperty("ProductID", out var productIds) || productIds.ValueKind == JsonValueKind.Null)
                        { unscoped = true; continue; }
                        if (productIds.GetArrayLength() == 0) unscoped = true;
                        foreach (var product in productIds.EnumerateArray())
                        {
                            var productId = Text(product, 128);
                            if (!products.ContainsKey(productId) || ++count > maximumRecords) throw Invalid();
                            statuses.Add(new(productId, statusType));
                        }
                    }
                var fixes = new List<MsrcRemediation>();
                if (vulnerability.TryGetProperty("Remediations", out var remediations) && remediations.ValueKind != JsonValueKind.Null)
                    foreach (var remediation in remediations.EnumerateArray())
                    {
                        if (!remediation.TryGetProperty("Type", out var type) || type.GetInt32() != 2) continue;
                        var kb = remediation.TryGetProperty("Description", out var description) && description.ValueKind == JsonValueKind.Object
                            ? OptionalText(description, "Value", 256) : null;
                        var build = OptionalTrimmedText(remediation, "FixedBuild", 128);
                        var supersedence = OptionalMultiline(remediation, "Supercedence", 8192);
                        var subtype = OptionalText(remediation, "SubType", 256);
                        foreach (var product in remediation.GetProperty("ProductID").EnumerateArray())
                        {
                            var productId = Text(product, 128);
                            if (!products.ContainsKey(productId) || ++count > maximumRecords) throw Invalid();
                            fixes.Add(new(productId, kb, build, supersedence, subtype));
                        }
                    }
                var severities = new List<MsrcSeverity>();
                if (vulnerability.TryGetProperty("Threats", out var threats) && threats.ValueKind == JsonValueKind.Array)
                    foreach (var threat in threats.EnumerateArray())
                    {
                        if (threat.ValueKind != JsonValueKind.Object || !threat.TryGetProperty("Type", out var type) ||
                            !type.TryGetInt32(out var threatType) || threatType != 3 ||
                            !threat.TryGetProperty("Description", out var description) || description.ValueKind != JsonValueKind.Object ||
                            !description.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.String ||
                            value.GetString() is not ("Critical" or "Important" or "Moderate" or "Low") ||
                            !threat.TryGetProperty("ProductID", out var productIds) || productIds.ValueKind != JsonValueKind.Array) continue;
                        foreach (var product in productIds.EnumerateArray())
                        {
                            var productId = Text(product, 128);
                            if (!products.ContainsKey(productId) || ++count > maximumRecords) throw Invalid();
                            severities.Add(new(productId, value.GetString()!));
                        }
                    }
                if (++count > maximumRecords) throw Invalid();
                advisories.Add(new(cve, unscoped, statuses, fixes, actionNotice, severities));
            }
            return new(id, modified, hasVulnerabilities, products, ambiguous, duplicates, advisories);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid MSRC CVRF document.", error); }
    }

    private static string RequiredText(JsonElement value, string property, int limit) => Text(value.GetProperty(property), limit);
    private static string? OptionalText(JsonElement value, string property, int limit)
    {
        if (!value.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        return field.ValueKind == JsonValueKind.String && field.GetString() == "" ? null : Text(field, limit);
    }
    private static string? OptionalMultiline(JsonElement value, string property, int limit)
    {
        if (!value.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        var text = field.GetString();
        if (text is { Length: > 0 } && text.Length > limit) throw Invalid();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= limit && !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')) ? text : throw Invalid();
    }
    private static string? OptionalTrimmedText(JsonElement value, string property, int limit)
    {
        if (!value.TryGetProperty(property, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        var raw = field.GetString();
        if (raw is null || raw.Length > limit) throw Invalid();
        var text = raw.Trim();
        if (text.Length == 0) return null;
        return text.Any(char.IsControl) ? throw Invalid() : text;
    }
    private static string Text(JsonElement value, int limit)
    {
        var text = value.GetString();
        return text is { Length: > 0 } && text.Length <= limit && !text.Any(char.IsControl) ? text : throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid MSRC CVRF document.");
}

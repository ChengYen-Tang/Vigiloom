using System.Text;
using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

internal sealed record AdvisoryPackage(string Ecosystem, string Name, string VulnerableRange, string? FirstPatchedVersion);
internal sealed record AdvisoryRecord(string Id, string? CveId, DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? WithdrawnAt, IReadOnlyList<AdvisoryPackage> Packages, string OriginalJson);

/// <summary>Validates the reviewed GitHub advisory subset without interpreting ecosystem ranges.</summary>
internal static class AdvisoryPageValidator
{
    internal static IReadOnlyList<AdvisoryRecord> Validate(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 100)
                throw new InvalidDataException("Invalid advisory page size.");
            var result = new List<AdvisoryRecord>(root.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.EnumerateArray())
            {
                var id = RequiredString(item, "ghsa_id");
                if (!ValidId(id) || !ids.Add(id) || RequiredString(item, "type") != "reviewed")
                    throw new InvalidDataException("Invalid or duplicate advisory identifier or type.");
                var published = RequiredTime(item, "published_at");
                var updated = RequiredTime(item, "updated_at");
                var withdrawn = OptionalTime(item, "withdrawn_at");
                var cve = OptionalString(item, "cve_id");
                var vulnerabilities = item.GetProperty("vulnerabilities");
                if (vulnerabilities.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid advisory vulnerabilities.");
                var packages = new List<AdvisoryPackage>(vulnerabilities.GetArrayLength());
                foreach (var vulnerability in vulnerabilities.EnumerateArray())
                {
                    var package = vulnerability.GetProperty("package");
                    packages.Add(new(RequiredString(package, "ecosystem"), RequiredString(package, "name"),
                        RequiredString(vulnerability, "vulnerable_version_range"), OptionalString(vulnerability, "first_patched_version")));
                }
                result.Add(new(id, cve, published, updated, withdrawn, packages, item.GetRawText()));
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Invalid advisory page.", error);
        }
    }

    private static string RequiredString(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Required advisory field is empty.");
    }

    private static string? OptionalString(JsonElement item, string name) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static DateTimeOffset RequiredTime(JsonElement item, string name) => ParseTime(item.GetProperty(name));

    private static DateTimeOffset? OptionalTime(JsonElement item, string name) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
        ParseTime(value);

    private static DateTimeOffset ParseTime(JsonElement value)
    {
        var text = value.GetString();
        if (text is null || !(text.EndsWith('Z') || text.Length >= 6 &&
            (text[^6] == '+' || text[^6] == '-') && text[^3] == ':') || !value.TryGetDateTimeOffset(out var time))
            throw new InvalidDataException("Invalid advisory timestamp.");
        return time.ToUniversalTime();
    }

    internal static bool ValidId(string id)
    {
        if (id.Length != 19 || !id.StartsWith("GHSA-", StringComparison.Ordinal) || id[9] != '-' || id[14] != '-') return false;
        for (var i = 5; i < id.Length; i++)
            if (i is not (9 or 14) && !(char.IsAsciiDigit(id[i]) || id[i] is >= 'a' and <= 'z')) return false;
        return true;
    }
}

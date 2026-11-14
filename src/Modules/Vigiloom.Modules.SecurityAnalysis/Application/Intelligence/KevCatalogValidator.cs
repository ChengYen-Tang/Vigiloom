using System.Globalization;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Validates a bounded CISA KEV snapshot before it is eligible for staging.</summary>
internal static class KevCatalogValidator
{
    internal const int DefaultMaximumBytes = 16 * 1024 * 1024;
    internal const int DefaultMaximumRecords = 50_000;
    internal const string SchemaRevision = "a795af4c4afc47df12906b4c04e656156cf06493";
    private const string SchemaResource = "Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Schemas.cisa-kev.json";
    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

    internal static KevCatalogMetadata Validate(ReadOnlyMemory<byte> content, int maximumBytes, int maximumRecords)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecords);
        if (content.IsEmpty || content.Length > maximumBytes) throw new InvalidDataException("KEV catalog size is outside the allowed limit.");
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(content.Span);
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("vulnerabilities", out var records) || records.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("count", out var declared) || declared.ValueKind != JsonValueKind.Number ||
                !declared.TryGetInt32(out var declaredCount) || declaredCount != records.GetArrayLength() || declaredCount > maximumRecords)
                throw new InvalidDataException("KEV catalog count is invalid or exceeds the allowed limit.");
            if (!Schema.Value.Evaluate(root).IsValid) throw new InvalidDataException("KEV catalog does not match the supported schema.");
            var version = root.GetProperty("catalogVersion").GetString();
            var release = root.GetProperty("dateReleased");
            var releaseText = release.GetString();
            if (string.IsNullOrWhiteSpace(version) || version.Length > 128 || releaseText is null ||
                !(releaseText.EndsWith('Z') || (releaseText.Length >= 6 &&
                    (releaseText[^6] == '+' || releaseText[^6] == '-') && releaseText[^3] == ':')) ||
                !release.TryGetDateTimeOffset(out var releasedAt))
                throw new InvalidDataException("KEV catalog version or release time is invalid.");
            var count = records.GetArrayLength();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records.EnumerateArray())
            {
                var id = record.GetProperty("cveID").GetString()!;
                if (!ids.Add(id) || !ValidDate(record.GetProperty("dateAdded")) || !ValidDate(record.GetProperty("dueDate")))
                    throw new InvalidDataException("KEV catalog contains a duplicate CVE or invalid date.");
            }
            return new(version, releasedAt.ToUniversalTime(), count);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("KEV catalog is not valid JSON.", error);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("KEV catalog is not valid UTF-8.", error);
        }
    }

    private static bool ValidDate(JsonElement value) => DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(KevCatalogValidator).Assembly.GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException("The pinned KEV schema is unavailable.");
        using var document = JsonDocument.Parse(stream);
        return JsonSchema.Build(document.RootElement.Clone());
    }
}

internal sealed record KevCatalogMetadata(string Version, DateTimeOffset ReleasedAt, int RecordCount);

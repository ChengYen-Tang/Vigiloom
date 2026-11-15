using System.Globalization;
using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record MsrcCatalogEntry(string Id, DateTimeOffset ModifiedAt);

/// <summary>Parses the complete update index. Pagination or malformed data cannot be mistaken for complete coverage.</summary>
internal static class MsrcCatalogReader
{
    internal static IReadOnlyList<MsrcCatalogEntry> Read(ReadOnlyMemory<byte> json, int maximumBytes, int maximumEntries)
    {
        if (maximumBytes <= 0 || maximumEntries <= 0 || json.Length == 0 || json.Length > maximumBytes)
            throw new InvalidDataException("MSRC catalog exceeds its bounds.");
        try
        {
            var reader = new Utf8JsonReader(json.Span, new JsonReaderOptions { MaxDepth = 16 });
            var keys = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) keys.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) keys.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !keys.Peek().Add(reader.GetString()!)) throw Invalid();
            }
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.TryGetProperty("@odata.nextLink", out _) || root.TryGetProperty("@odata.deltaLink", out _)) throw Invalid();
            var entries = new List<MsrcCatalogEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var item in root.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("ID").GetString();
                var modified = item.GetProperty("CurrentReleaseDate").GetString();
                if (!ValidId(id) || !seen.Add(id!) || modified is null || !(modified.EndsWith("Z", StringComparison.Ordinal) || modified.EndsWith("+00:00", StringComparison.Ordinal)) || !DateTimeOffset.TryParse(modified, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.Zero || ++count > maximumEntries) throw Invalid();
                entries.Add(new(id!, timestamp));
            }
            if (entries.Count == 0) throw Invalid();
            return entries;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid MSRC catalog.", error); }
    }

    internal static bool ValidId(string? id)
    {
        if (id is null || id.Length is < 8 or > 16 || id[4] != '-' ||
            id.AsSpan(0, 4).ContainsAnyExceptInRange('0', '9') || id.AsSpan(0, 4).SequenceEqual("0000")) return false;
        var month = id.Substring(5, 3);
        if (!new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" }.Contains(month, StringComparer.OrdinalIgnoreCase)) return false;
        return id.Length == 8 || id[8] == '-' && id.Length > 9 && id.AsSpan(9).IndexOfAnyExceptInRange('A', 'Z') < 0;
    }

    private static InvalidDataException Invalid() => new("Invalid MSRC catalog.");
}

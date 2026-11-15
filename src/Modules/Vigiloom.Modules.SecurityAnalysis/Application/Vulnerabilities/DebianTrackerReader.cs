using System.Text.Json;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record DebianTrackerRecord(string SourcePackage, string AdvisoryId, string Release,
    DebianAdvisoryState State, string? FixedVersion);

/// <summary>Reads the tracker's package/advisory/release hierarchy. Callers must stage atomically until the whole input validates.</summary>
internal static class DebianTrackerReader
{
    internal static void Read(ReadOnlySpan<byte> json, int maximumBytes, int maximumRecords, Action<DebianTrackerRecord> stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (maximumBytes <= 0 || maximumRecords <= 0 || json.Length == 0 || json.Length > maximumBytes)
            throw new InvalidDataException("Debian tracker input exceeds its configured bounds.");
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
            Expect(ref reader, JsonTokenType.StartObject);
            var packages = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var package = Key(ref reader, packages);
                Expect(ref reader, JsonTokenType.StartObject);
                var advisories = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var advisory = Key(ref reader, advisories);
                    Expect(ref reader, JsonTokenType.StartObject);
                    var properties = new HashSet<string>(StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        var property = Key(ref reader, properties);
                        if (property != "releases") { if (!reader.Read()) throw Invalid(); reader.Skip(); continue; }
                        Expect(ref reader, JsonTokenType.StartObject);
                        var releases = new HashSet<string>(StringComparer.Ordinal);
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            var release = Key(ref reader, releases);
                            Expect(ref reader, JsonTokenType.StartObject);
                            var fields = new HashSet<string>(StringComparer.Ordinal);
                            string? status = null, fixedVersion = null;
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                var field = Key(ref reader, fields);
                                if (!reader.Read()) throw Invalid();
                                if (field is "status" or "fixed_version")
                                {
                                    if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null)) throw Invalid();
                                    var value = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                                    if (value is { Length: > 2048 }) throw Invalid();
                                    if (field == "status") status = value; else fixedVersion = value;
                                }
                                else reader.Skip();
                            }
                            if (reader.TokenType != JsonTokenType.EndObject || string.IsNullOrWhiteSpace(status) || ++count > maximumRecords) throw Invalid();
                            var normalized = DebianTrackerStatus.Normalize(status, fixedVersion);
                            stage(new(package, advisory, release, normalized.State, normalized.FixedVersion));
                        }
                        if (reader.TokenType != JsonTokenType.EndObject) throw Invalid();
                    }
                    if (reader.TokenType != JsonTokenType.EndObject || !properties.Contains("releases")) throw Invalid();
                }
                if (reader.TokenType != JsonTokenType.EndObject) throw Invalid();
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || count == 0) throw Invalid();
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Debian tracker JSON.", error); }
    }

    private static string Key(ref Utf8JsonReader reader, HashSet<string> seen)
    {
        if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
        var key = reader.GetString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || !seen.Add(key)) throw Invalid();
        return key;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (!reader.Read() || reader.TokenType != type) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid or incomplete Debian tracker record.");
}

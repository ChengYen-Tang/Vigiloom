using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Platforms.Windows.Logs;

public enum EventLogReadStatus { Record, End, CursorMissing, Oversized }
public sealed record EventLogObservation(long RecordId, DateTimeOffset? OccurredAt, string Xml,
    string? Provider, int? EventId, string? Level, string? User, string? Process);
public sealed record EventLogReadResult(EventLogReadStatus Status, EventLogObservation? Observation = null, string? NextCursor = null);

/// <summary>Reads one Windows event and validates the previously committed bookmark.</summary>
[SupportedOSPlatform("windows")]
public static class EventLogSource
{
    private const int MaximumCursorCharacters = 4095;
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static EventLogReadResult ReadNext(string channel, string? cursor, CancellationToken token)
        => ReadNext(channel, cursor, AgentLogBatchCodec.MaximumContentCharacters, token);

    internal static EventLogReadResult ReadNext(string channel, string? cursor, int maximumCharacters, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 512 || channel.Any(char.IsControl))
            throw new ArgumentException("A valid event channel is required.", nameof(channel));
        token.ThrowIfCancellationRequested();
        var saved = cursor is null ? null : DecodeCursor(cursor);
        using var query = new NativeEventQuery(channel);
        if (saved is not null)
        {
            try
            {
                query.Seek(saved.Bookmark);
                using var previous = query.Next();
                if (previous is null) return new(EventLogReadStatus.CursorMissing);
                var previousEvent = Read(previous, channel, maximumCharacters);
                if (previousEvent.RecordId != saved.RecordId ||
                    !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(previousEvent.Digest), Convert.FromHexString(saved.Digest)))
                    return new(EventLogReadStatus.CursorMissing);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 259 or 1168 or 15011 or 15012)
            { return new(EventLogReadStatus.CursorMissing); }
        }
        token.ThrowIfCancellationRequested();
        using var record = query.Next();
        if (record is null) return new(EventLogReadStatus.End);
        var current = Read(record, channel, maximumCharacters);
        var next = JsonSerializer.Serialize(new Cursor(current.RecordId, current.Bookmark, current.Digest));
        if (next.Length > MaximumCursorCharacters) throw new InvalidDataException("Event bookmark exceeds the supported cursor size.");
        if (current.Xml is null) return new(EventLogReadStatus.Oversized, NextCursor: next);
        return new(EventLogReadStatus.Record, Parse(current.RecordId, current.Xml), next);
    }

    private static ReadEvent Read(NativeEventQuery.EventHandle record, string channel, int maximumCharacters)
    {
        var bookmark = NativeEventQuery.Bookmark(record);
        var bookmarkNode = ParseXml(bookmark).Descendants().SingleOrDefault(node => node.Name.LocalName == "Bookmark");
        var bookmarkChannel = (string?)bookmarkNode?.Attribute("Channel");
        if (!string.Equals(bookmarkChannel, channel, StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse((string?)bookmarkNode?.Attribute("RecordId"), NumberStyles.None, CultureInfo.InvariantCulture, out var recordId) || recordId < 0)
            throw new InvalidDataException("The event bookmark does not identify the requested channel and record.");
        var rendered = NativeEventQuery.RenderXml(record, maximumCharacters);
        // Oversized events have no complete content hash; system identity and size are a weaker fingerprint across channel resets.
        string fingerprint;
        if (rendered.Text is null)
        {
            var identity = NativeEventQuery.SystemIdentity(record);
            if (identity.RecordId != (ulong)recordId) throw new InvalidDataException("Event and bookmark record IDs differ.");
            fingerprint = FormattableString.Invariant($"oversized:{bookmark}:{rendered.RequiredBytes}:{identity.CreatedFileTime}");
        }
        else fingerprint = rendered.Text;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        return new(recordId, bookmark, rendered.Text, digest);
    }

    internal static EventLogObservation Parse(long recordId, string xml)
    {
        var system = ParseXml(xml).Root?.Element(EventNamespace + "System") ?? throw new InvalidDataException("Event system fields are missing.");
        if (!long.TryParse((string?)system.Element(EventNamespace + "EventRecordID"), NumberStyles.None, CultureInfo.InvariantCulture, out var observedId) || observedId != recordId)
            throw new InvalidDataException("Event and bookmark record IDs differ.");
        var eventIdText = (string?)system.Element(EventNamespace + "EventID");
        var eventId = int.TryParse(eventIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) && parsedId >= 0 ? parsedId : (int?)null;
        var timeText = (string?)system.Element(EventNamespace + "TimeCreated")?.Attribute("SystemTime");
        var occurredAt = DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime) ? parsedTime : (DateTimeOffset?)null;
        return new(recordId, occurredAt, xml, ValidOptional((string?)system.Element(EventNamespace + "Provider")?.Attribute("Name"), 256), eventId,
            ValidOptional((string?)system.Element(EventNamespace + "Level"), 64), ValidOptional((string?)system.Element(EventNamespace + "Security")?.Attribute("UserID"), 256),
            ValidOptional((string?)system.Element(EventNamespace + "Execution")?.Attribute("ProcessID"), 256));
    }

    private static string? ValidOptional(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl) ? value : null;

    private static XDocument ParseXml(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = AgentLogBatchCodec.MaximumContentCharacters });
        try { return XDocument.Load(reader); }
        catch (XmlException exception) { throw new InvalidDataException("Invalid event XML.", exception); }
    }

    private static Cursor DecodeCursor(string cursor)
    {
        if (cursor.Length is 0 or > MaximumCursorCharacters) throw new InvalidDataException("Invalid event cursor.");
        try
        {
            var decoded = JsonSerializer.Deserialize<Cursor>(cursor);
            if (decoded is null || decoded.RecordId < 0 || string.IsNullOrEmpty(decoded.Bookmark) ||
                decoded.Bookmark.Length > MaximumCursorCharacters || decoded.Digest is null || decoded.Digest.Length != 64 ||
                !decoded.Digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid event cursor.");
            return decoded;
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid event cursor.", exception); }
    }

    private sealed record ReadEvent(long RecordId, string Bookmark, string? Xml, string Digest);
    private sealed record Cursor(long RecordId, string Bookmark, string Digest);
}

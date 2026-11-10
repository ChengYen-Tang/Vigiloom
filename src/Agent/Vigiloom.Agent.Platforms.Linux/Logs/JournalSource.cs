using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Platforms.Linux.Logs;

public enum JournalReadStatus { Record, End, CursorMissing, Oversized }
public sealed record JournalField(string Encoding, string Value);
public sealed record JournalObservation(DateTimeOffset OccurredAt, string Content, string? Provider = null,
    string? Level = null, string? User = null, string? Process = null);
public sealed record JournalReadResult(JournalReadStatus Status, JournalObservation? Observation = null, string? NextCursor = null);

/// <summary>Reads one local journal entry, preserving text and binary fields without loss.</summary>
[SupportedOSPlatform("linux")]
public static partial class JournalSource
{
    private const int MaximumCursorCharacters = 4095; // The durable adapter adds one prefix character.
    private const int MaximumFields = 256;
    private const int MaximumFieldBytes = 32 * 1024;
    private const int MaximumEntryBytes = 40 * 1024;

    // The journal handle is opened, used and closed synchronously on the same thread.
    public static unsafe JournalReadResult ReadNext(string? cursor, CancellationToken token)
    {
        if (cursor is not null && (cursor.Length is 0 or > MaximumCursorCharacters || cursor.Contains('\0')))
            throw new ArgumentException("Invalid journal cursor.", nameof(cursor));
        token.ThrowIfCancellationRequested();
        Check(Native.Open(out var journal, 1)); // SD_JOURNAL_LOCAL_ONLY
        try
        {
            Check(Native.SetDataThreshold(journal, MaximumFieldBytes + 1));
            Check(cursor is null ? Native.SeekHead(journal) : Native.SeekCursor(journal, cursor));
            var moved = Check(Native.Next(journal));
            if (cursor is not null)
            {
                if (moved == 0 || Check(Native.TestCursor(journal, cursor)) == 0)
                    return new(JournalReadStatus.CursorMissing);
                token.ThrowIfCancellationRequested();
                moved = Check(Native.Next(journal));
            }
            if (moved == 0) return new(JournalReadStatus.End);
            Check(Native.GetCursor(journal, out var pointer));
            string next;
            try
            {
                if (pointer == 0) throw new InvalidDataException("Journal cursor is missing.");
                var bytes = (byte*)pointer;
                var length = 0;
                while (length <= MaximumCursorCharacters && bytes[length] != 0) length++;
                if (length is 0 or > MaximumCursorCharacters) throw new InvalidDataException("Journal cursor exceeds the supported size.");
                next = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(bytes, length));
            }
            finally { NativeMemory.Free((void*)pointer); }
            if (next.Length is 0 or > MaximumCursorCharacters) throw new InvalidDataException("Journal cursor exceeds the supported size.");
            Check(Native.GetRealtimeUsec(journal, out var usec));
            var occurredAt = DateTimeOffset.UnixEpoch.AddTicks(checked((long)usec * 10));
            var fields = new List<JournalField>();
            var total = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var result = Native.EnumerateData(journal, out var data, out var length);
                // Linux E2BIG / ENOBUFS: the field or compressed entry cannot be read at this size.
                if (result is -7 or -105) return new(JournalReadStatus.Oversized, NextCursor: next);
                Check(result);
                if (result == 0) break;
                if (length > MaximumFieldBytes || fields.Count == MaximumFields || length > (nuint)(MaximumEntryBytes - total))
                    return new(JournalReadStatus.Oversized, NextCursor: next);
                var size = checked((int)length);
                total += size;
                var bytes = new ReadOnlySpan<byte>((void*)data, size);
                fields.Add(JournalFieldEncoding.Encode(bytes));
            }
            var content = JsonSerializer.Serialize(fields);
            if (content.Length > AgentLogBatchCodec.MaximumContentCharacters)
                return new(JournalReadStatus.Oversized, NextCursor: next);
            var metadata = JournalMetadata.FromFields(fields);
            return new(JournalReadStatus.Record, new(occurredAt, content, metadata.Provider, metadata.Level, metadata.User, metadata.Process), next);
        }
        finally { Native.Close(journal); }
    }

    private static int Check(int result) => result < 0 ? throw new Win32Exception(-result) : result;

    private static partial class Native
    {
        private const string Library = "libsystemd.so.0";
        [LibraryImport(Library, EntryPoint = "sd_journal_open")]
        internal static partial int Open(out nint journal, int flags);
        [LibraryImport(Library, EntryPoint = "sd_journal_close")]
        internal static partial void Close(nint journal);
        [LibraryImport(Library, EntryPoint = "sd_journal_set_data_threshold")]
        internal static partial int SetDataThreshold(nint journal, nuint size);
        [LibraryImport(Library, EntryPoint = "sd_journal_seek_head")]
        internal static partial int SeekHead(nint journal);
        [LibraryImport(Library, EntryPoint = "sd_journal_seek_cursor", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SeekCursor(nint journal, string cursor);
        [LibraryImport(Library, EntryPoint = "sd_journal_test_cursor", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int TestCursor(nint journal, string cursor);
        [LibraryImport(Library, EntryPoint = "sd_journal_next")]
        internal static partial int Next(nint journal);
        [LibraryImport(Library, EntryPoint = "sd_journal_get_cursor")]
        internal static partial int GetCursor(nint journal, out nint cursor);
        [LibraryImport(Library, EntryPoint = "sd_journal_get_realtime_usec")]
        internal static partial int GetRealtimeUsec(nint journal, out ulong usec);
        [LibraryImport(Library, EntryPoint = "sd_journal_enumerate_data")]
        internal static partial int EnumerateData(nint journal, out nint data, out nuint length);
    }
}

internal sealed record JournalMetadata(string? Provider, string? Level, string? User, string? Process)
{
    internal static JournalMetadata FromFields(IReadOnlyList<JournalField> fields)
    {
        string? provider = null, level = null, user = null, process = null;
        foreach (var field in fields)
        {
            if (field.Encoding != "utf8") continue;
            var delimiter = field.Value.IndexOf('=');
            if (delimiter <= 0 || delimiter == field.Value.Length - 1) continue;
            var name = field.Value.AsSpan(0, delimiter);
            var value = field.Value.AsSpan(delimiter + 1);
            if (value.Length > 256 || ContainsControl(value) || value.IsWhiteSpace()) continue;
            if (provider is null && name.SequenceEqual("SYSLOG_IDENTIFIER")) provider = value.ToString();
            else if (level is null && name.SequenceEqual("PRIORITY") && value.Length == 1 && value[0] is >= '0' and <= '7') level = value.ToString();
            else if (user is null && name.SequenceEqual("_UID") && IsNumber(value)) user = value.ToString();
            else if (process is null && name.SequenceEqual("_PID") && IsNumber(value)) process = value.ToString();
        }
        return new(provider, level, user, process);
    }

    private static bool IsNumber(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (!char.IsAsciiDigit(c)) return false;
        return value.Length > 0;
    }

    private static bool ContainsControl(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (char.IsControl(c)) return true;
        return false;
    }
}

internal static class JournalFieldEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static JournalField Encode(ReadOnlySpan<byte> bytes)
    {
        try { return new("utf8", StrictUtf8.GetString(bytes)); }
        catch (DecoderFallbackException) { return new("base64", Convert.ToBase64String(bytes)); }
    }
}

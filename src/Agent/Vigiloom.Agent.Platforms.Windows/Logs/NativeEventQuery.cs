using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Windows.Logs;

internal readonly record struct NativeEventText(string? Text, uint RequiredBytes, bool Oversized);
internal readonly record struct NativeEventIdentity(ulong RecordId, ulong CreatedFileTime);

/// <summary>Native local-channel query. Query and event handles must stay on the creating thread.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeEventQuery : IDisposable
{
    private readonly EventHandle query;

    internal NativeEventQuery(string channel)
    {
        var raw = Native.Query(0, channel, "*", 0x101); // channel path, forward direction
        if (raw == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        query = new(raw);
    }

    internal EventHandle? Next()
    {
        if (!Native.Next(query, 1, out var raw, 0, 0, out var returned))
        {
            var error = Marshal.GetLastPInvokeError();
            if (raw != 0) Native.Close(raw);
            if (error == 259) return null; // ERROR_NO_MORE_ITEMS
            throw new Win32Exception(error);
        }
        if (returned != 1 || raw == 0)
        {
            if (raw != 0) Native.Close(raw);
            throw new InvalidDataException("The event query returned an invalid handle.");
        }
        return new(raw);
    }

    internal static NativeEventText RenderXml(EventHandle record, int maximumCharacters) => RenderText(record, 1, maximumCharacters);

    internal static string Bookmark(EventHandle record)
    {
        var raw = Native.CreateBookmark(null);
        if (raw == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var bookmark = new EventHandle(raw);
        if (!Native.UpdateBookmark(bookmark, record)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var rendered = RenderText(bookmark, 2, 4095);
        return rendered.Text ?? throw new InvalidDataException("Event bookmark is too large.");
    }

    internal static unsafe NativeEventIdentity SystemIdentity(EventHandle record)
    {
        var raw = Native.CreateRenderContext(0, 0, 1); // EvtRenderContextSystem
        if (raw == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var context = new EventHandle(raw);
        if (Native.Render(context.DangerousGetHandle(), record, 0, 0, null, out var required, out _))
            throw new InvalidDataException("Event system render unexpectedly returned no buffer.");
        var error = Marshal.GetLastPInvokeError();
        if (error != 122) throw new Win32Exception(error);
        const uint maxSystemBytes = 64 * 1024;
        if (required < 10 * 16 || required > maxSystemBytes) throw new InvalidDataException("Event system properties exceed the supported size.");
        var buffer = new byte[required];
        fixed (byte* pointer = buffer)
        {
            if (!Native.Render(context.DangerousGetHandle(), record, 0, required, pointer, out var used, out var count))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (used > required || used < 10 * 16 || count < 10 || count > used / 16)
                throw new InvalidDataException("Invalid event system property buffer.");
            var time = MemoryMarshal.Read<EventVariant>(buffer.AsSpan(8 * 16, 16));
            var id = MemoryMarshal.Read<EventVariant>(buffer.AsSpan(9 * 16, 16));
            if (time.Type != 17 || id.Type != 10)
                throw new InvalidDataException("Event system identity is unavailable.");
            return new(id.Value, time.Value);
        }
    }

    internal void Seek(string bookmarkXml)
    {
        var raw = Native.CreateBookmark(bookmarkXml);
        if (raw == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var bookmark = new EventHandle(raw);
        if (!Native.Seek(query, 0, bookmark, 0, 0x10004)) // strict, relative to bookmark
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static unsafe NativeEventText RenderText(EventHandle record, int flags, int maximumCharacters)
    {
        if (maximumCharacters < 1 || maximumCharacters > 65_536) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var maximumBytes = checked((uint)((maximumCharacters + 1) * sizeof(char)));
        if (Native.Render(0, record, flags, 0, null, out var required, out _))
            throw new InvalidDataException("Event render unexpectedly returned no buffer.");
        var error = Marshal.GetLastPInvokeError();
        if (error != 122) throw new Win32Exception(error); // ERROR_INSUFFICIENT_BUFFER
        if (required < 2 || (required & 1) != 0) throw new InvalidDataException("Invalid event render buffer size.");
        if (required > maximumBytes) return new(null, required, true);
        var buffer = new char[required / sizeof(char)];
        fixed (char* pointer = buffer)
        {
            if (!Native.Render(0, record, flags, required, pointer, out var used, out _))
            {
                error = Marshal.GetLastPInvokeError();
                if (error == 122 && used > maximumBytes) return new(null, used, true);
                throw new Win32Exception(error);
            }
            if (used < 2 || used > required || (used & 1) != 0 || buffer[used / sizeof(char) - 1] != '\0')
                throw new InvalidDataException("Invalid event render output.");
            return new(new string(buffer, 0, checked((int)(used / sizeof(char) - 1))), used, false);
        }
    }

    public void Dispose() => query.Dispose();

    internal sealed class EventHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal EventHandle(nint handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => Native.Close(handle);
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct EventVariant
    {
        [FieldOffset(0)] public ulong Value;
        [FieldOffset(8)] public uint Count;
        [FieldOffset(12)] public uint Type;
    }

    private static partial class Native
    {
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtQuery", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint Query(nint session, string path, string query, int flags);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtNext", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Next(EventHandle resultSet, int count, out nint events, int timeout, int flags, out int returned);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtRender", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool Render(nint context, EventHandle fragment, int flags, uint size, void* buffer, out uint used, out uint properties);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtCreateBookmark", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        internal static partial nint CreateBookmark(string? xml);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtCreateRenderContext", SetLastError = true)]
        internal static partial nint CreateRenderContext(int valuePathsCount, nint valuePaths, int flags);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtUpdateBookmark", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateBookmark(EventHandle bookmark, EventHandle record);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtSeek", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Seek(EventHandle resultSet, long position, EventHandle bookmark, int timeout, int flags);
        [LibraryImport("wevtapi.dll", EntryPoint = "EvtClose", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool Close(nint handle);
    }
}

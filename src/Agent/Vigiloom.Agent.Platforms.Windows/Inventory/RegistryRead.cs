using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>Bounded reads from an already opened registry key. Native status codes are returned directly.</summary>
[SupportedOSPlatform("windows")]
internal static partial class RegistryRead
{
    internal const int NoMoreItems = 259;
    private const int Success = 0;
    private const int NotFound = 2;

    internal static unsafe bool Stamp(SafeRegistryHandle key, out uint subkeys, out long lastWrite) =>
        RegQueryInfoKey(key, null, null, null, out subkeys, null, null, null, null, null, null, out lastWrite) == Success;

    internal static unsafe int Name(SafeRegistryHandle key, uint index, out string? name)
    {
        char* buffer = stackalloc char[256];
        uint length = 256;
        var status = RegEnumKeyEx(key, index, buffer, &length, null, null, null, null);
        name = status == Success ? new string(buffer, 0, (int)length) : null;
        return status;
    }

    internal static unsafe bool Text(SafeRegistryHandle key, string valueName, out string? value)
    {
        // A single bounded call also handles values that grow concurrently: ERROR_MORE_DATA is incomplete.
        char* buffer = stackalloc char[2049];
        uint bytes = 2049 * sizeof(char);
        var status = RegQueryValueEx(key, valueName, null, out var type, (byte*)buffer, &bytes);
        value = null;
        if (status == NotFound) return true;
        if (status != Success || type is not (1 or 2) || bytes % 2 != 0) return false;
        var length = (int)(bytes / sizeof(char));
        if (length > 0 && buffer[length - 1] == '\0') length--;
        if (length > 2048) return false;
        var text = new ReadOnlySpan<char>(buffer, length);
        if (text.Contains('\0')) return false;
        value = text.ToString();
        return true;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumKeyExW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int RegEnumKeyEx(SafeRegistryHandle key, uint index, char* name, uint* length,
        void* reserved, void* className, void* classLength, void* lastWriteTime);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int RegQueryValueEx(SafeRegistryHandle key, string valueName, void* reserved,
        out uint type, byte* data, uint* size);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int RegQueryInfoKey(SafeRegistryHandle key, void* className, void* classLength,
        void* reserved, out uint subkeys, void* maximumSubkey, void* maximumClass, void* values,
        void* maximumValueName, void* maximumValue, void* security, out long lastWrite);
}

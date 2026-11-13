using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Vigiloom.Agent.Core.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Integrity;

public enum RegistryValueReadStatus { Success, PermissionDenied, Missing, TooLarge, Changed, Error }
public sealed record RegistryValueDigest(RegistryValueReadStatus Status, uint? NativeType = null, int? Bytes = null, string? Digest = null);

/// <summary>Reads and hashes raw registry bytes without expansion or returning their contents.</summary>
[SupportedOSPlatform("windows")]
public static partial class RegistryValueReader
{
    public static unsafe RegistryValueDigest Read(SafeRegistryHandle key, string name, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);
        if (maximumBytes is < 0 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        uint size = 0;
        var status = RegQueryValueEx(key, name, 0, out var type, null, ref size);
        if (status != 0) return new(Map(status));
        if (size > maximumBytes) return new(RegistryValueReadStatus.TooLarge);
        var bytes = new byte[(int)size];
        try
        {
            var actual = size;
            int result;
            uint actualType;
            fixed (byte* buffer = bytes)
            {
                byte dummy = 0;
                result = RegQueryValueEx(key, name, 0, out actualType, size == 0 ? &dummy : buffer, ref actual);
            }
            if (result != 0) return new(Map(result));
            if (actual != size || actualType != type) return new(RegistryValueReadStatus.Changed);
            return new(RegistryValueReadStatus.Success, type, bytes.Length, IntegrityHash.RegistryValue(type, bytes));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static RegistryValueReadStatus Map(int status) => status switch
    {
        2 => RegistryValueReadStatus.Missing,
        5 => RegistryValueReadStatus.PermissionDenied,
        234 => RegistryValueReadStatus.Changed,
        _ => RegistryValueReadStatus.Error
    };

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, out uint type, byte* data, ref uint size);
}

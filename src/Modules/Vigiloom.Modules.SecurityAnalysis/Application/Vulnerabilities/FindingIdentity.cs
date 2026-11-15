using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Hashes length-delimited UTF-16 code units; source and device are separate database key columns.</summary>
internal static class FindingIdentity
{
    internal static byte[] Hash(string packageKey, string advisoryId)
    {
        if (string.IsNullOrWhiteSpace(packageKey) || packageKey.Length > 2048 ||
            string.IsNullOrWhiteSpace(advisoryId) || advisoryId.Length > 128)
            throw new ArgumentException("Invalid finding identity.");
        Span<byte> data = stackalloc byte[8 + 2 * (packageKey.Length + advisoryId.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(data, packageKey.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], advisoryId.Length);
        var offset = 8;
        foreach (var ch in packageKey)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data[offset..], ch);
            offset += 2;
        }
        foreach (var ch in advisoryId)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data[offset..], ch);
            offset += 2;
        }
        return SHA256.HashData(data);
    }
}

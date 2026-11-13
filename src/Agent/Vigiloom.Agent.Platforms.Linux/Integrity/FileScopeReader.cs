using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Win32.SafeHandles;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Platforms.Linux.Integrity;

/// <summary>Bounded handle-relative local filesystem scan; no symlink or mount traversal.</summary>
[SupportedOSPlatform("linux")]
public static class FileScopeReader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<IntegrityReadResult> ReadAsync(IntegrityScope scope, CancellationToken token)
    {
        if (!scope.IsValid() || scope.Kind != IntegrityScopeKind.Directory) throw new ArgumentException("Invalid file scope.", nameof(scope));
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !scope.Root.StartsWith('/') || scope.Root.Contains('\\'))
            return new(IntegrityScanOutcome.Unsupported, [], false);
        using var root = LinuxFiles.OpenRoot(scope.Root, out var rootError);
        if (root is null) return new(rootError is 1 or 13 ? IntegrityScanOutcome.PermissionDenied :
            rootError is 38 or 22 ? IntegrityScanOutcome.Unsupported : IntegrityScanOutcome.Error, [], false);
        if (!LinuxFiles.IsLocal(root, out _)) return new(IntegrityScanOutcome.Unsupported, [], false);
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude("**/*");
        foreach (var exclusion in scope.Exclusions) matcher.AddExclude(exclusion);
        var entries = new List<IntegrityItem>();
        var stack = new Stack<(string Path, string[] Parts, int Depth, LinuxFiles.Stamp? Expected)>();
        var scannedDirectories = new List<(string Path, LinuxFiles.Stamp Stamp)>();
        stack.Push((".", [], 0, null));
        var buffer = new byte[32 * 1024];
        var visited = 0;
        long readBytes = 0;
        IntegrityPartialReason reasons = 0;
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (path, parts, depth, expected) = stack.Pop();
            using var directory = LinuxFiles.OpenChild(root, path, directory: true, inspectOnly: false, out var error);
            if (directory is null) { reasons |= Reason(error); continue; }
            if (!LinuxFiles.TryStamp(directory, out var before, out error)) { reasons |= Reason(error); continue; }
            if (expected is { } original && original != before) { reasons |= IntegrityPartialReason.ChangedDuringScan; continue; }
            scannedDirectories.Add((path, before));
            var security = Security(directory, before, ref reasons);
            if (security is null) continue;
            try
            {
                if (!Add(new(IntegrityKey.Create(IntegrityItemKind.Directory, parts), IntegrityItemKind.Directory,
                    SecurityDigest: security, Owner: $"uid:{before.Uid}"))) break;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var length = LinuxFiles.ReadEntries(directory, buffer, out error);
                    if (length < 0) { reasons |= Reason(error); break; }
                    if (length == 0) break;
                    var offset = 0;
                    while (offset < length)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!LinuxFiles.TryEntry(buffer.AsSpan(0, length), ref offset, out var raw)) { reasons |= IntegrityPartialReason.Other; break; }
                        string name;
                        try { name = Utf8.GetString(raw); }
                        catch (DecoderFallbackException) { reasons |= IntegrityPartialReason.Other; continue; }
                        if (name is "." or "..") continue;
                        if (++visited > scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; break; }
                        var childParts = parts.Append(name).ToArray();
                        if (!matcher.Match(string.Join('/', childParts)).HasMatches) continue;
                        var childPath = path == "." ? name : $"{path}/{name}";
                        using var inspect = LinuxFiles.OpenChild(root, childPath, directory: false, inspectOnly: true, out error);
                        if (inspect is null) { reasons |= Reason(error); continue; }
                        if (!LinuxFiles.TryStamp(inspect, out var stamp, out error)) { reasons |= Reason(error); continue; }
                        var type = (ushort)(stamp.Mode & LinuxFiles.TypeMask);
                        if (type == LinuxFiles.SymlinkType) continue;
                        if (type == LinuxFiles.DirectoryType)
                        {
                            if (depth >= scope.MaximumDepth || stack.Count + entries.Count >= scope.MaximumEntries)
                                reasons |= IntegrityPartialReason.LimitReached;
                            else stack.Push((childPath, childParts, depth + 1, stamp));
                            continue;
                        }
                        if (type != LinuxFiles.FileType) { reasons |= IntegrityPartialReason.SourceUnavailable; continue; }
                        if (stamp.Size > (ulong)scope.MaximumFileBytes || stamp.Size > (ulong)Math.Max(0, scope.MaximumScanBytes - readBytes))
                        { reasons |= IntegrityPartialReason.LimitReached; continue; }
                        using var handle = LinuxFiles.OpenChild(root, childPath, directory: false, inspectOnly: false, out error);
                        if (handle is null) { reasons |= Reason(error); continue; }
                        if (!LinuxFiles.TryStamp(handle, out var opened, out error) || opened != stamp)
                        { reasons |= IntegrityPartialReason.ChangedDuringScan; continue; }
                        var fileSecurity = Security(handle, opened, ref reasons);
                        if (fileSecurity is null) continue;
                        await using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
                        var hash = await IntegrityHash.FileAsync(stream, (long)stamp.Size, token);
                        readBytes += hash.BytesRead;
                        if (hash.Digest is null || !LinuxFiles.TryStamp(handle, out var after, out error) || after != opened || hash.BytesRead != (long)stamp.Size)
                        { reasons |= IntegrityPartialReason.ChangedDuringScan; continue; }
                        if (!Add(new(IntegrityKey.Create(IntegrityItemKind.File, childParts), IntegrityItemKind.File,
                            Convert.ToHexStringLower(hash.Digest), fileSecurity, $"uid:{opened.Uid}"))) break;
                    }
                    if (visited > scope.MaximumEntries) break;
                }
                if (!LinuxFiles.TryStamp(directory, out var afterDirectory, out error) || afterDirectory != before)
                    reasons |= IntegrityPartialReason.ChangedDuringScan;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
            { reasons |= ex is UnauthorizedAccessException ? IntegrityPartialReason.AccessDenied : IntegrityPartialReason.Other; }
        }
        // Traversal is not an atomic filesystem snapshot. Revalidate all observed directory
        // identities and stamps so a replaced ancestor cannot publish a mixed tree as complete.
        using var reopenedRoot = LinuxFiles.OpenRoot(scope.Root, out _);
        if (reopenedRoot is null || !LinuxFiles.TryStamp(root, out var originalRoot, out _) ||
            !LinuxFiles.TryStamp(reopenedRoot, out var currentRoot, out _) || originalRoot != currentRoot)
            reasons |= IntegrityPartialReason.ChangedDuringScan;
        foreach (var (path, observed) in scannedDirectories)
        {
            token.ThrowIfCancellationRequested();
            if (!LinuxFiles.DirectoryMatches(root, path, observed))
                reasons |= IntegrityPartialReason.ChangedDuringScan;
        }
        return new(reasons == 0 ? IntegrityScanOutcome.Complete : IntegrityScanOutcome.Partial, entries, false,
            reasons == 0 ? null : reasons);

        bool Add(IntegrityItem item)
        {
            if (entries.Count >= scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; return false; }
            entries.Add(item);
            return true;
        }
    }

    private static string? Security(SafeFileHandle handle, LinuxFiles.Stamp stamp, ref IntegrityPartialReason reasons)
    {
        var acl = LinuxFiles.ReadAcl(handle, false, out var error);
        if (acl is null) { reasons |= Reason(error); return null; }
        var defaultAcl = (stamp.Mode & LinuxFiles.TypeMask) == LinuxFiles.DirectoryType ? LinuxFiles.ReadAcl(handle, true, out error) : [];
        if (defaultAcl is null) { reasons |= Reason(error); return null; }
        Span<byte> metadata = stackalloc byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(metadata, stamp.Mode);
        BinaryPrimitives.WriteUInt32BigEndian(metadata[2..], stamp.Uid);
        BinaryPrimitives.WriteUInt32BigEndian(metadata[6..], stamp.Gid);
        BinaryPrimitives.WriteUInt32BigEndian(metadata[10..], (uint)acl.Length);
        BinaryPrimitives.WriteUInt32BigEndian(metadata[14..], (uint)defaultAcl.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metadata);
        hash.AppendData(acl);
        hash.AppendData(defaultAcl);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static IntegrityPartialReason Reason(int error) => error switch
    {
        1 or 13 => IntegrityPartialReason.AccessDenied,
        2 or 11 or 18 or 20 or 40 => IntegrityPartialReason.ChangedDuringScan,
        7 or 34 => IntegrityPartialReason.LimitReached,
        _ => IntegrityPartialReason.Other
    };
}

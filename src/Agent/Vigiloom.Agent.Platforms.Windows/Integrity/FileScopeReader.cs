using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Win32.SafeHandles;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Integrity;

/// <summary>Bounded, handle-relative collection without following reparse points.</summary>
[SupportedOSPlatform("windows")]
public static class FileScopeReader
{
    public static async Task<IntegrityReadResult> ReadAsync(IntegrityScope scope, CancellationToken token)
    {
        if (!scope.IsValid() || scope.Kind != IntegrityScopeKind.Directory) throw new ArgumentException("Invalid file scope.", nameof(scope));
        SafeFileHandle root;
        try { root = WindowsFiles.OpenScope(scope.Root); }
        catch (PlatformNotSupportedException) { return new(IntegrityScanOutcome.Unsupported, [], false); }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException or ArgumentException)
        { return new(ex is UnauthorizedAccessException || ex is Win32Exception { NativeErrorCode: 5 } ? IntegrityScanOutcome.PermissionDenied : IntegrityScanOutcome.Error, [], false); }
        using (root)
        {
            var scan = new Scan(scope, root);
            return await scan.RunAsync(token);
        }
    }

    private sealed class Scan(IntegrityScope scope, SafeFileHandle root)
    {
        private readonly Matcher matcher = CreateMatcher(scope);
        private readonly List<IntegrityItem> items = [];
        private readonly List<(string[] Parts, WindowsFiles.Stamp Stamp)> directories = [];
        private readonly byte[] buffer = new byte[32 * 1024];
        private IntegrityPartialReason reasons;
        private int visited;
        private long bytesRead;

        public async Task<IntegrityReadResult> RunAsync(CancellationToken token)
        {
            var initial = WindowsFiles.ReadStamp(root);
            await ReadDirectoryAsync(root, [], 0, initial, token);
            try
            {
                using var reopened = WindowsFiles.OpenScope(scope.Root);
                if (WindowsFiles.ReadStamp(reopened) != initial) reasons |= IntegrityPartialReason.ChangedDuringScan;
                foreach (var (parts, stamp) in directories)
                {
                    token.ThrowIfCancellationRequested();
                    if (!MatchesDirectory(parts, stamp)) reasons |= IntegrityPartialReason.ChangedDuringScan;
                }
            }
            catch (Exception ex) when (IsReadError(ex)) { reasons |= Reason(ex); }
            return new(reasons == 0 ? IntegrityScanOutcome.Complete : IntegrityScanOutcome.Partial, items, false,
                reasons == 0 ? null : reasons);
        }

        private async Task ReadDirectoryAsync(SafeFileHandle directory, string[] parts, int depth, WindowsFiles.Stamp initial, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                directories.Add((parts, initial));
                var security = WindowsFiles.ReadSecurity(directory);
                if (!Add(new(IntegrityKey.Create(IntegrityItemKind.Directory, parts), IntegrityItemKind.Directory,
                    SecurityDigest: security.Digest, Owner: security.Owner))) return;
                var restart = true;
                while (visited <= scope.MaximumEntries && WindowsFiles.ReadNames(directory, buffer, restart) is { } names)
                {
                    restart = false;
                    foreach (var name in names)
                    {
                        token.ThrowIfCancellationRequested();
                        if (++visited > scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; break; }
                        var childParts = parts.Append(name).ToArray();
                        if (!matcher.Match(string.Join('/', childParts)).HasMatches) continue;
                        try
                        {
                            using var child = WindowsFiles.OpenChild(directory, name);
                            var stamp = WindowsFiles.ReadStamp(child);
                            if ((stamp.Attributes & WindowsFiles.ReparsePoint) != 0) continue;
                            if (stamp.Volume != initial.Volume) { reasons |= IntegrityPartialReason.SourceUnavailable; continue; }
                            if ((stamp.Attributes & WindowsFiles.Directory) != 0)
                            {
                                if (depth >= scope.MaximumDepth) reasons |= IntegrityPartialReason.LimitReached;
                                else await ReadDirectoryAsync(child, childParts, depth + 1, stamp, token);
                                continue;
                            }
                            if (stamp.Size > (ulong)scope.MaximumFileBytes || stamp.Size > (ulong)Math.Max(0, scope.MaximumScanBytes - bytesRead))
                            { reasons |= IntegrityPartialReason.LimitReached; continue; }
                            var fileSecurity = WindowsFiles.ReadSecurity(child);
                            await using var stream = new FileStream(child, FileAccess.Read, 64 * 1024, isAsync: false);
                            var hash = await IntegrityHash.FileAsync(stream, (long)stamp.Size, token);
                            bytesRead += hash.BytesRead;
                            if (hash.Digest is null || hash.BytesRead != (long)stamp.Size || WindowsFiles.ReadStamp(child) != stamp)
                            { reasons |= IntegrityPartialReason.ChangedDuringScan; continue; }
                            if (!Add(new(IntegrityKey.Create(IntegrityItemKind.File, childParts), IntegrityItemKind.File,
                                Convert.ToHexStringLower(hash.Digest), fileSecurity.Digest, fileSecurity.Owner))) break;
                        }
                        catch (Exception ex) when (IsReadError(ex)) { reasons |= Reason(ex); }
                    }
                }
                if (WindowsFiles.ReadStamp(directory) != initial) reasons |= IntegrityPartialReason.ChangedDuringScan;
            }
            catch (Exception ex) when (IsReadError(ex)) { reasons |= Reason(ex); }
        }

        private bool MatchesDirectory(string[] parts, WindowsFiles.Stamp expected)
        {
            SafeFileHandle? previous = null;
            try
            {
                var parent = root;
                foreach (var part in parts)
                {
                    var child = WindowsFiles.OpenChild(parent, part);
                    previous?.Dispose();
                    previous = child;
                    parent = child;
                    var stamp = WindowsFiles.ReadStamp(child);
                    if ((stamp.Attributes & (WindowsFiles.Directory | WindowsFiles.ReparsePoint)) != WindowsFiles.Directory || stamp.Volume != expected.Volume)
                        return false;
                }
                return WindowsFiles.ReadStamp(parent) == expected;
            }
            finally { previous?.Dispose(); }
        }

        private bool Add(IntegrityItem item)
        {
            if (items.Count >= scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; return false; }
            items.Add(item);
            return true;
        }

        private static Matcher CreateMatcher(IntegrityScope policy)
        {
            var result = new Matcher(StringComparison.OrdinalIgnoreCase);
            result.AddInclude("**/*");
            foreach (var exclusion in policy.Exclusions) result.AddExclude(exclusion);
            return result;
        }
        private static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or DecoderFallbackException;
        private static IntegrityPartialReason Reason(Exception ex) => ex switch
        {
            UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => IntegrityPartialReason.AccessDenied,
            Win32Exception { NativeErrorCode: 2 or 3 or 32 or 33 or 4390 } => IntegrityPartialReason.ChangedDuringScan,
            _ => IntegrityPartialReason.Other
        };
    }
}

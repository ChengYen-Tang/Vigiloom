using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

/// <summary>Reads local passwd/group files, not directory-service or shadow credentials.</summary>
public static class LocalPrincipalReader
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumLineLength = 8192;
    private const int MaximumEntries = 20_000;
    private const int MaximumNameLength = InventoryChunkCodec.MaximumMemberLength;

    [SupportedOSPlatform("linux")]
    public static Task<InventoryReadResult<LocalPrincipal>> ReadAccountsAsync(CancellationToken token) => ReadFileAsync("/etc/passwd", false, token);

    [SupportedOSPlatform("linux")]
    public static Task<InventoryReadResult<LocalPrincipal>> ReadGroupsAsync(CancellationToken token) => ReadFileAsync("/etc/group", true, token);

    [SupportedOSPlatform("linux")]
    private static async Task<InventoryReadResult<LocalPrincipal>> ReadFileAsync(string path, bool group, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
            if (!FileIdentity.TryRead(file.SafeFileHandle, path, out var initial)) return new(InventoryReadStatus.Error, []);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await file.ReadAsync(buffer, token);
                if (read == 0) break;
                if (content.Length + read > MaximumBytes) return new(InventoryReadStatus.Partial, []);
                await content.WriteAsync(buffer.AsMemory(0, read), token);
            }
            content.Position = 0;
            using var reader = new StreamReader(content, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var parsed = Parse(reader, group, token);
            return !FileIdentity.TryRead(file.SafeFileHandle, path, out var final) || initial != final
                ? parsed with { Status = InventoryReadStatus.Partial } : parsed;
        }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException) { return new(InventoryReadStatus.Error, []); }
    }

    internal static InventoryReadResult<LocalPrincipal> Parse(TextReader reader, bool group, CancellationToken token)
    {
        var items = new List<LocalPrincipal>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, GroupMembers>(StringComparer.Ordinal);
        var partial = false;
        var memberCount = 0;
        var limitReached = false;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            if (line.Length > MaximumLineLength) { partial = true; continue; }
            var fields = line.Split(':');
            if (fields.Length != (group ? 4 : 7) || !ValidName(fields[0]) ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                partial = true;
                continue;
            }
            if (!group)
            {
                if (items.Count >= MaximumEntries || !names.Add(fields[0]) ||
                    !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid)) { partial = true; continue; }
                items.Add(new(fields[0], id, gid, []));
            }
            else
            {
                var members = fields[3].Length == 0 ? [] : fields[3].Split(',');
                if (members.Any(member => !ValidName(member))) { partial = true; continue; }
                if (!groups.TryGetValue(fields[0], out var existing))
                {
                    if (groups.Count >= MaximumEntries) { partial = true; continue; }
                    existing = new(id);
                    groups.Add(fields[0], existing);
                }
                if (existing.Id != id) { partial = true; continue; }
                foreach (var member in members)
                {
                    if (existing.Members.Count >= InventoryChunkCodec.MaximumMembers && !existing.Members.Contains(member)) { partial = true; continue; }
                    if (!existing.Members.Contains(member) && memberCount >= InventoryChunkCodec.MaximumSnapshotMembers)
                    {
                        partial = true;
                        limitReached = true;
                        break;
                    }
                    if (existing.Members.Add(member)) memberCount++;
                }
                if (limitReached) break;
            }
        }
        if (group)
            foreach (var (name, value) in groups) items.Add(new(name, value.Id, null, value.Members.ToArray()));
        return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, items);
    }

    private sealed class GroupMembers(uint id)
    {
        public uint Id { get; } = id;
        public HashSet<string> Members { get; } = new(StringComparer.Ordinal);
    }

    private static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumNameLength && !value.Any(char.IsControl);
}

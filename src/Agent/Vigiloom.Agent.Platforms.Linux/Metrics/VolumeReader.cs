using System.Runtime.Versioning;
using System.Text;
using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Metrics;

/// <summary>Finds known local Linux mount types before requesting capacity.</summary>
public static class VolumeReader
{
    public const int MaximumMountLines = 8192;
    private static readonly HashSet<string> LocalTypes = new(StringComparer.Ordinal)
        { "ext2", "ext3", "ext4", "xfs", "btrfs", "zfs", "vfat", "exfat", "ntfs3", "overlay", "overlayfs", "squashfs" };

    [SupportedOSPlatform("linux")]
    public static async Task<VolumeSample> ReadAsync(CancellationToken token)
    {
        using var reader = new StreamReader("/proc/self/mountinfo");
        var lines = new List<string>();
        var limited = false;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (lines.Count == MaximumMountLines) { limited = true; break; }
            lines.Add(line);
        }
        var candidates = ParseMounts(lines);
        // Incomplete or malformed metadata might omit a covering remote mount.
        if (limited || candidates.Malformed) return new([], candidates.InvalidMounts, limited || candidates.Truncated);
        var volumes = new List<VolumeCapacity>(candidates.Mounts.Count);
        var unavailable = candidates.InvalidMounts;
        foreach (var mount in candidates.Mounts)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var drive = new DriveInfo(mount);
                var total = drive.TotalSize;
                var available = drive.AvailableFreeSpace;
                if (total <= 0 || available < 0 || available > total) { unavailable++; continue; }
                volumes.Add(new(mount, total, available));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                unavailable++;
            }
        }
        return new(volumes, unavailable, limited || candidates.Truncated);
    }

    /// <summary>Parses mount metadata only; remote and unrecognized types are not probed.</summary>
    public static MountCandidates ParseMounts(IEnumerable<string> lines)
    {
        var mounts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unknownOrRemote = new HashSet<string>(StringComparer.Ordinal);
        var local = new List<string>();
        var invalid = 0;
        var malformed = false;
        var truncated = false;
        foreach (var line in lines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) { invalid++; malformed = true; continue; }
            var right = line.AsSpan(separator + 3);
            var end = right.IndexOf(' ');
            if (end < 0) { invalid++; malformed = true; continue; }
            var fields = line.AsSpan(0, separator).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || !TryDecodeMount(fields[4], out var mount)) { invalid++; malformed = true; continue; }
            if (LocalTypes.Contains(right[..end].ToString())) local.Add(mount);
            else unknownOrRemote.Add(mount);
        }
        foreach (var mount in local)
        {
            // An unknown or remote mount can hide an older local mount at the same
            // path or below it. Do not probe paths whose visible backing is unclear.
            if (IsCovered(mount, unknownOrRemote)) { invalid++; continue; }
            if (!seen.Add(mount)) continue;
            if (mounts.Count == Vigiloom.Agent.Core.Metrics.VolumeReader.MaximumVolumes) { truncated = true; continue; }
            mounts.Add(mount);
        }
        return new(mounts, invalid, truncated, malformed);
    }

    private static bool IsCovered(string mount, HashSet<string> unknownOrRemote)
    {
        var current = mount;
        while (true)
        {
            if (unknownOrRemote.Contains(current)) return true;
            if (current == "/") return false;
            var slash = current.LastIndexOf('/');
            current = slash <= 0 ? "/" : current[..slash];
        }
    }

    private static bool TryDecodeMount(string encoded, out string mount)
    {
        var builder = new StringBuilder(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '\\')
            {
                if (i + 3 >= encoded.Length) { mount = ""; return false; }
                var escape = encoded.AsSpan(i + 1, 3);
                if (escape.SequenceEqual("040")) c = ' ';
                else if (escape.SequenceEqual("134")) c = '\\';
                else { mount = ""; return false; }
                i += 3;
            }
            if (char.IsControl(c)) { mount = ""; return false; }
            builder.Append(c);
        }
        mount = builder.ToString();
        return mount.StartsWith('/') && mount.Length <= 256;
    }
}

public sealed record MountCandidates(IReadOnlyList<string> Mounts, int InvalidMounts, bool Truncated, bool Malformed);

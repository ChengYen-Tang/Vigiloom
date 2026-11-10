namespace Vigiloom.Agent.Core.Metrics;

/// <summary>Capacity available to the Agent's account on a local mounted volume, in bytes.</summary>
public sealed record VolumeCapacity(string Mount, long TotalBytes, long AvailableBytes);
public sealed record VolumeSample(IReadOnlyList<VolumeCapacity> Volumes, int UnavailableVolumes, bool Truncated);

public static class VolumeReader
{
    public const int MaximumVolumes = 256;

    /// <summary>Reads Windows local and removable volumes; this is not a physical-disk inventory.</summary>
    public static VolumeSample ReadWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var drives = DriveInfo.GetDrives();
        Array.Sort(drives, static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        var values = new List<VolumeCapacity>(Math.Min(drives.Length, MaximumVolumes));
        var unavailable = 0;
        var eligible = 0;
        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                eligible++;
                if (eligible > MaximumVolumes) continue;
                if (!drive.IsReady) { unavailable++; continue; }
                var total = drive.TotalSize;
                var available = drive.AvailableFreeSpace;
                if (total <= 0 || available < 0 || available > total || string.IsNullOrWhiteSpace(drive.Name) ||
                    drive.Name.Length > 256 || drive.Name.Any(char.IsControl))
                {
                    unavailable++;
                    continue;
                }
                values.Add(new(drive.Name, total, available));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unavailable++;
            }
        }
        return new(values, unavailable, eligible > MaximumVolumes);
    }
}

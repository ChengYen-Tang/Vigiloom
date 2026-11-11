using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

/// <summary>Reads the RPM database; other package ecosystems are separate sources.</summary>
public static class RpmReader
{
    [SupportedOSPlatform("linux")]
    public static Task<InventoryReadResult<InstalledSoftware>> ReadAsync(CancellationToken token) =>
        PackageCommand.ReadAsync("/usr/bin/rpm", ["-qa", "--qf", "%{NAME}\t%{EPOCHNUM}:%{VERSION}-%{RELEASE}\t%{ARCH}\t%{VENDOR}\n"], Parse, token);

    internal static (PackageCommand.LineStatus Status, InstalledSoftware? Item) Parse(string line)
    {
        var columns = line.Split('\t');
        return columns.Length == 4 ? PackageFields.Read(columns, 0, 1, 2, 3, true) : (PackageCommand.LineStatus.Invalid, null);
    }
}

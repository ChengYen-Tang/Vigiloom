using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

/// <summary>Reads dpkg's installed package view; packages outside dpkg are a separate source.</summary>
public static class DpkgReader
{
    [SupportedOSPlatform("linux")]
    public static Task<InventoryReadResult<InstalledSoftware>> ReadAsync(CancellationToken token) =>
        PackageCommand.ReadAsync("/usr/bin/dpkg-query",
            ["-W", "-f=${db:Status-Abbrev}\t${binary:Package}\t${Version}\t${Architecture}\t${Maintainer}\t${source:Package}\t${source:Version}\n"], Parse, token);

    internal static (PackageCommand.LineStatus Status, InstalledSoftware? Item) Parse(string line)
    {
        var columns = line.Split('\t');
        if (columns.Length != 7 || columns[0].Length != 3) return (PackageCommand.LineStatus.Invalid, null);
        var status = columns[0];
        if (!"uihrp".Contains(status[0]) || status[2] != ' ') return (PackageCommand.LineStatus.Invalid, null);
        if (status[1] is 'n' or 'c') return (PackageCommand.LineStatus.NotInstalled, null);
        if (status[1] != 'i') return (PackageCommand.LineStatus.Invalid, null);
        var (result, item) = PackageFields.Read(columns, 1, 2, 3, 4, false);
        if (result != PackageCommand.LineStatus.Installed || item is null) return (result, item);
        // Missing source metadata remains unknown; package-name equality does not prove provenance.
        return (result, item with { SourcePackageName = string.IsNullOrWhiteSpace(columns[5]) ? null : columns[5],
            SourcePackageVersion = string.IsNullOrWhiteSpace(columns[6]) ? null : columns[6] });
    }
}

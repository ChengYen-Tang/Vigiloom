using Microsoft.Win32;
using System.ComponentModel;
using System.Reflection;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using Linux = Vigiloom.Agent.Platforms.Linux.Inventory;
using Windows = Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Hosting.Inventory;

/// <summary>Maps source-native observations to the common bounded inventory contract.</summary>
internal sealed class InventoryCollector(TimeProvider clock)
{
    internal async Task<InventoryReadResult<InventoryEntry>> ReadAsync(InventorySource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source == InventorySource.HostNetwork) return HostHardwareReader.ReadNetwork(token);
        if (source == InventorySource.HostVolumes)
        {
            try
            {
                if (OperatingSystem.IsLinux()) return HostHardwareReader.FromVolumes(await Vigiloom.Agent.Platforms.Linux.Metrics.VolumeReader.ReadAsync(token));
                if (OperatingSystem.IsWindows()) return HostHardwareReader.FromVolumes(Vigiloom.Agent.Core.Metrics.VolumeReader.ReadWindows());
                return new(InventoryReadStatus.Unsupported, []);
            }
            catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { return new(InventoryReadStatus.Error, []); }
        }
        if (source == InventorySource.HostSystem)
        {
            var host = HostSystemReader.Read(clock);
            string? distribution = null, distributionVersion = null;
            DateTimeOffset? boot = null;
            ulong? memory = null;
            var complete = true;
            var family = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other";
            if (OperatingSystem.IsLinux())
            {
                var details = await Linux.SystemDetailsReader.ReadAsync(token);
                (distribution, distributionVersion, boot, complete) = (details.DistributionId, details.DistributionVersion, details.BootedAt, details.Complete);
                try { memory = (await Vigiloom.Agent.Platforms.Linux.Metrics.MemoryReader.ReadAsync(token))?.TotalBytes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (OperatingSystem.IsWindows())
            {
                boot = Windows.BootTimeReader.Read(token);
                complete = boot is not null;
                try { memory = Vigiloom.Agent.Platforms.Windows.Metrics.MemoryReader.Read().TotalBytes; }
                catch (Win32Exception) { }
            }
            else complete = false;
            var agentVersion = NormalizeAgentVersion(typeof(InventoryCollector).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            complete &= agentVersion is not null && memory is > 0;
            return new(complete ? InventoryReadStatus.Complete : InventoryReadStatus.Partial,
                [new("host", host.HostName, host.KernelOrPlatformVersion, host.OsArchitecture,
                    OperatingSystem: host.OperatingSystem, ProcessArchitecture: host.ProcessArchitecture,
                    Runtime: host.RuntimeVersion, AvailableProcessorCount: host.AvailableProcessorCount,
                    OperatingSystemFamily: family, DistributionId: distribution, DistributionVersion: distributionVersion,
                    BootedAt: boot, AgentVersion: agentVersion, TotalMemoryBytes: memory)]);
        }
        if (OperatingSystem.IsLinux())
        {
            switch (source)
            {
                case InventorySource.LinuxDpkg: return Software(await Linux.DpkgReader.ReadAsync(token));
                case InventorySource.LinuxRpm: return Software(await Linux.RpmReader.ReadAsync(token));
                case InventorySource.LinuxAccounts: return Principals(await Linux.LocalPrincipalReader.ReadAccountsAsync(token), false);
                case InventorySource.LinuxGroups: return Principals(await Linux.LocalPrincipalReader.ReadGroupsAsync(token), true);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            switch (source)
            {
                case InventorySource.WindowsSoftware32: return Software(Windows.UninstallRegistryReader.Read(RegistryView.Registry32, token));
                case InventorySource.WindowsSoftware64 when Environment.Is64BitOperatingSystem:
                    return Software(Windows.UninstallRegistryReader.Read(RegistryView.Registry64, token));
                case InventorySource.WindowsQuickFix:
                    var patches = Windows.QuickFixReader.Read(token);
                    return new(patches.Status, patches.Items.Select(x => new InventoryEntry(x.SourceKey, x.Description ?? x.SourceKey, InstalledOn: x.InstalledOn)).ToArray());
                case InventorySource.WindowsAccounts:
                    return WindowsPrincipals(Windows.LocalPrincipalReader.ReadAccounts(token));
                case InventorySource.WindowsGroups:
                    return WindowsPrincipals(Windows.LocalPrincipalReader.ReadGroups(token));
            }
        }
        return new(InventoryReadStatus.Unsupported, []);
    }

    private static InventoryReadResult<InventoryEntry> Software(InventoryReadResult<InstalledSoftware> source) =>
        new(source.Status, source.Items.Select(x => new InventoryEntry(x.SourceKey, x.Name, x.Version, x.Architecture, x.Supplier)).ToArray());

    private static InventoryReadResult<InventoryEntry> Principals(InventoryReadResult<LocalPrincipal> source, bool groups) =>
        new(source.Status, source.Items.Select(x => new InventoryEntry(x.Name, x.Name, NumericId: x.Id,
            PrimaryGroupId: groups ? null : x.PrimaryGroupId, Members: groups ? x.Members : null)).ToArray());

    private static InventoryReadResult<InventoryEntry> WindowsPrincipals(InventoryReadResult<Windows.WindowsLocalPrincipal> source) =>
        new(source.Status, source.Items.Select(x => new InventoryEntry(x.Sid, x.Name, Members: x.MemberSids)).ToArray());

    internal static string? NormalizeAgentVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var version = informationalVersion.Split('+', 2)[0];
        return string.IsNullOrWhiteSpace(version) || version.Length > 256 || version.Any(char.IsControl) ? null : version;
    }
}

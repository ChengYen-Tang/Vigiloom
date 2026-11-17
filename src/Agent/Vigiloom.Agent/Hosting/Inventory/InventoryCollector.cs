using Microsoft.Win32;
using System.ComponentModel;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using Linux = Vigiloom.Agent.Platforms.Linux.Inventory;
using Windows = Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Hosting.Inventory;

/// <summary>Maps source-native observations to the common bounded inventory contract.</summary>
internal sealed class InventoryCollector(TimeProvider clock)
{
    internal async Task<InventoryReadResult<InventoryEntry>> ReadAsync(InventorySource source, CancellationToken token, bool collectCommandLine = false)
    {
        token.ThrowIfCancellationRequested();
        if (source == InventorySource.HostProcesses)
        {
            var processes = Vigiloom.Agent.Core.Processes.ProcessReader.Read(token, collectCommandLine);
            if (processes.Status is not (InventoryReadStatus.Complete or InventoryReadStatus.Partial))
                return new(processes.Status, []);
            var partial = processes.Status == InventoryReadStatus.Partial;
            var reasons = processes.PartialReason ?? 0;
            var parents = new Dictionary<int, Vigiloom.Agent.Platforms.Windows.Processes.ProcessParent>();
            if (OperatingSystem.IsWindows())
            {
                var sourceParents = Vigiloom.Agent.Platforms.Windows.Processes.ParentProcessReader.Read(token);
                if (sourceParents.Status != InventoryReadStatus.Complete)
                {
                    partial = true;
                    reasons |= sourceParents.Status == InventoryReadStatus.PermissionDenied ? InventoryPartialReason.AccessDenied : InventoryPartialReason.Other;
                }
                foreach (var parent in sourceParents.Items)
                    if (!parents.TryAdd(parent.ProcessId, parent)) partial = true;
            }
            var entries = new List<InventoryEntry>(processes.Items.Count);
            foreach (var process in processes.Items)
            {
                token.ThrowIfCancellationRequested();
                var account = process.RunAs;
                var parentId = process.ParentProcessId;
                var path = process.ExecutablePath;
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        var details = Vigiloom.Agent.Platforms.Windows.Processes.ProcessIdentityReader.Read(process, collectCommandLine);
                        account = details.Account;
                        path = details.ExecutablePath;
                        reasons |= details.PartialReason ?? 0;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or Win32Exception or ArgumentException)
                    {
                        if (ex is UnauthorizedAccessException or System.Security.SecurityException || ex is Win32Exception { NativeErrorCode: 5 }) reasons |= InventoryPartialReason.AccessDenied;
                        account = null;
                    }
                    if (account is null) partial = true;
                    if (collectCommandLine && path is null) partial = true;
                    if (parents.TryGetValue(process.ProcessId, out var parent) && parent.CreationMicroseconds == process.StartedAt.UtcTicks / 10)
                        parentId = parent.ParentProcessId;
                    else partial = true;
                }
                entries.Add(new InventoryEntry(process.StableKey, process.Name,
                    NumericId: checked((uint)process.ProcessId), ProcessStartedAt: process.StartedAt,
                    ParentProcessId: parentId, RunAs: account, ExecutablePath: path,
                    CommandLine: path is null ? null : path + InventoryChunkCodec.MaskedArgumentsSuffix));
            }
            return new(partial ? InventoryReadStatus.Partial : processes.Status, entries,
                partial ? reasons == 0 ? InventoryPartialReason.Other : reasons : null);
        }
        if (source == InventorySource.HostServices)
        {
            InventoryReadResult<Vigiloom.Agent.Core.Processes.ServiceObservation> services;
            if (OperatingSystem.IsWindows()) services = Vigiloom.Agent.Platforms.Windows.Processes.ServiceReader.Read(token);
            else if (OperatingSystem.IsLinux()) services = await Vigiloom.Agent.Platforms.Linux.Processes.ServiceReader.ReadAsync(token);
            else return new(InventoryReadStatus.Unsupported, []);
            return new(services.Status, services.Items.Select(x => new InventoryEntry(x.Key, x.Name,
                ServiceStatus: x.Status, StartupMode: x.StartupMode)).ToArray(),
                services.Status == InventoryReadStatus.Partial ? services.PartialReason ?? InventoryPartialReason.Other : null);
        }
        if (source == InventorySource.HostListeners)
        {
            InventoryReadResult<Vigiloom.Agent.Core.Processes.ListenerObservation> listeners;
            if (OperatingSystem.IsWindows()) listeners = Vigiloom.Agent.Platforms.Windows.Processes.ListenerReader.Read(token);
            else if (OperatingSystem.IsLinux()) listeners = await Vigiloom.Agent.Platforms.Linux.Processes.ListenerReader.ReadAsync(token);
            else return new(InventoryReadStatus.Unsupported, []);
            if (listeners.Status is InventoryReadStatus.Unsupported or InventoryReadStatus.Error)
            {
                var originalStatus = listeners.Status;
                listeners = Vigiloom.Agent.Core.Processes.BasicListenerReader.Read(token);
                if (listeners.Status == InventoryReadStatus.Partial)
                    listeners = listeners with { PartialReason = InventoryPartialReason.OwnerUnavailable |
                        (originalStatus == InventoryReadStatus.Unsupported ? InventoryPartialReason.ToolUnavailable : InventoryPartialReason.Other) };
            }
            return new(listeners.Status, listeners.Items.Distinct().Select(x => new InventoryEntry(
                $"listener:{x.Protocol}:{x.Address.Length}:{x.Address}:{x.Port}:{x.OwningProcessId?.ToString() ?? "unknown"}",
                $"{x.Protocol.ToUpperInvariant()} {x.Port}", Address: x.Address,
                Protocol: x.Protocol, Port: x.Port, OwningProcessId: x.OwningProcessId)).ToArray(),
                listeners.Status == InventoryReadStatus.Partial ? listeners.PartialReason ?? InventoryPartialReason.Other : null);
        }
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
            Windows.WindowsRelease? windowsRelease = null;
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
                var release = Windows.WindowsReleaseReader.Read(token);
                if (release.Status == InventoryReadStatus.Complete) windowsRelease = release.Items.Single();
                complete = boot is not null;
                try { memory = Vigiloom.Agent.Platforms.Windows.Metrics.MemoryReader.Read().TotalBytes; }
                catch (Win32Exception) { }
            }
            else complete = false;
            var agentVersion = AgentRuntimeInfo.Version;
            complete &= agentVersion is not null && memory is > 0;
            return new(complete ? InventoryReadStatus.Complete : InventoryReadStatus.Partial,
                [new("host", host.HostName, host.KernelOrPlatformVersion, host.OsArchitecture,
                    OperatingSystem: host.OperatingSystem, ProcessArchitecture: host.ProcessArchitecture,
                    Runtime: host.RuntimeVersion, AvailableProcessorCount: host.AvailableProcessorCount,
                    OperatingSystemFamily: family, DistributionId: distribution, DistributionVersion: distributionVersion,
                    BootedAt: boot, AgentVersion: agentVersion, TotalMemoryBytes: memory,
                    WindowsEdition: windowsRelease?.Edition, WindowsRelease: windowsRelease?.Release,
                    WindowsBuild: windowsRelease?.Build, WindowsRevision: windowsRelease?.Revision,
                    WindowsInstallationType: windowsRelease?.InstallationType)]);
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
                case InventorySource.WindowsUpdate:
                    return WindowsUpdates(await Windows.WindowsUpdateReader.ReadAsync(token));
                case InventorySource.WindowsAccounts:
                    return WindowsPrincipals(Windows.LocalPrincipalReader.ReadAccounts(token));
                case InventorySource.WindowsGroups:
                    return WindowsPrincipals(Windows.LocalPrincipalReader.ReadGroups(token));
            }
        }
        return new(InventoryReadStatus.Unsupported, []);
    }

    internal static InventoryReadResult<InventoryEntry> Software(InventoryReadResult<InstalledSoftware> source) =>
        new(source.Status, source.Items.Select(x => new InventoryEntry(x.SourceKey, x.Name, x.Version, x.Architecture, x.Supplier,
            SourcePackageName: x.SourcePackageName is not null && x.SourcePackageVersion is not null ? x.SourcePackageName : null,
            SourcePackageVersion: x.SourcePackageName is not null && x.SourcePackageVersion is not null ? x.SourcePackageVersion : null)).ToArray());

    internal static InventoryReadResult<InventoryEntry> WindowsUpdates(InventoryReadResult<Windows.WindowsUpdateObservation> source)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<InventoryEntry>(source.Items.Count);
        foreach (var update in source.Items)
        {
            var key = $"{update.UpdateId:D}:{update.Revision}";
            if (!keys.Add(key) || update.Cves.Any(x => x.Length > InventoryChunkCodec.MaximumMemberLength) ||
                update.KnowledgeBaseIds.Any(x => x.Length > InventoryChunkCodec.MaximumMemberLength))
                return new(InventoryReadStatus.Partial, []);
            entries.Add(new(key, update.Title, UpdateId: update.UpdateId, UpdateRevision: update.Revision,
                UpdateInstalled: update.Installed, UpdateRebootRequired: update.RebootRequired,
                UpdateCves: update.Cves, UpdateKbs: update.KnowledgeBaseIds, UpdateServerSelection: update.ServerSelection,
                UpdateServiceId: update.ServiceId, UpdateSupersededIds: update.SupersededUpdateIds));
        }
        return new(source.Status, entries);
    }

    private static InventoryReadResult<InventoryEntry> Principals(InventoryReadResult<LocalPrincipal> source, bool groups) =>
        new(source.Status, source.Items.Select(x => new InventoryEntry(x.Name, x.Name, NumericId: x.Id,
            PrimaryGroupId: groups ? null : x.PrimaryGroupId, Members: groups ? x.Members : null)).ToArray());

    private static InventoryReadResult<InventoryEntry> WindowsPrincipals(InventoryReadResult<Windows.WindowsLocalPrincipal> source) =>
        new(source.Status, source.Items.Select(x => new InventoryEntry(x.Sid, x.Name, Members: x.MemberSids)).ToArray());

}

using Vigiloom.Agent.Core.Baseline;
using Vigiloom.Agent.Platforms.Linux.Baseline;
using Vigiloom.Agent.Platforms.Linux.Inventory;
using Vigiloom.Agent.Platforms.Windows.Baseline;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using System.Globalization;

namespace Vigiloom.Agent.Hosting.Baseline;

/// <summary>Maps reviewed check identities to fixed read-only platform operations.</summary>
internal static class BaselineCollector
{
    public static async Task<(string OperatingSystem, BaselineObservation Result)> ReadAsync(BaselineCheck check, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (check is { Id: BaselineSources.LinuxShadowCheck, Platform: "linux", Operation: BaselineSources.LinuxShadowOperation,
                Target: BaselineSources.LinuxShadowTarget, Expected: "0640-or-stricter" })
        {
            if (!OperatingSystem.IsLinux()) return (SystemName(), new(BaselineOutcome.NotApplicable));
            var system = await SystemDetailsReader.ReadAsync(token);
            return ($"{system.DistributionId ?? "unknown"}:{system.DistributionVersion ?? "unknown"}",
                ShadowPermissions.Read(system.DistributionId, system.DistributionVersion));
        }
        if (check is { Id: BaselineSources.WindowsNetworkSelectionCheck, Platform: "windows", Operation: BaselineSources.WindowsRegistryOperation,
                Target: BaselineSources.WindowsNetworkSelectionTarget, Expected: "1" })
        {
            if (!OperatingSystem.IsWindows()) return (SystemName(), new(BaselineOutcome.NotApplicable));
            return NetworkSelection.ReadCurrent(token);
        }
        if (CustomBaselineCheck.IsValid(check))
        {
            if (check.Platform == "linux")
            {
                if (!OperatingSystem.IsLinux()) return (SystemName(), new(BaselineOutcome.NotApplicable));
                var system = await SystemDetailsReader.ReadAsync(token);
                if (system.DistributionId is null || system.DistributionVersion is null) return ("linux:unknown", new(BaselineOutcome.Unknown));
                var name = $"{system.DistributionId}:{system.DistributionVersion}";
                if (!check.Versions.Contains(name, StringComparer.Ordinal)) return (name, new(BaselineOutcome.NotApplicable));
                CustomBaselineCheck.TryMode(check.Expected, out var allowed);
                return (name, ShadowPermissions.ReadMode(check.Target, allowed));
            }
            if (!OperatingSystem.IsWindows()) return (SystemName(), new(BaselineOutcome.NotApplicable));
            var separator = check.Target.LastIndexOf('\\');
            var path = check.Target[5..separator];
            var value = check.Target[(separator + 1)..];
            var expected = uint.Parse(check.Expected, NumberStyles.None, CultureInfo.InvariantCulture);
            return NetworkSelection.ReadCurrent(path, value, expected, token);
        }
        return (SystemName(), new(BaselineOutcome.Unknown, "Unsupported baseline check"));
    }

    private static string SystemName() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unknown";
}

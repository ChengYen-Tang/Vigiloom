using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Vigiloom.Agent.Core.Baseline;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Baseline;

/// <summary>Reads the selected PowerSTIG registry setting; it does not change policy.</summary>
[SupportedOSPlatform("windows")]
public static class NetworkSelection
{
    private const string Path = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string ValueName = "DontDisplayNetworkSelectionUI";

    public static (string OperatingSystem, BaselineObservation Result) ReadCurrent(CancellationToken token) =>
        ReadCurrent(Path, ValueName, 1, token);

    public static (string OperatingSystem, BaselineObservation Result) ReadCurrent(string path, string valueName, uint expected, CancellationToken token)
    {
        var release = WindowsReleaseReader.Read(token);
        if (release.Status == InventoryReadStatus.PermissionDenied) return ("windows:unknown", new(BaselineOutcome.PermissionDenied));
        var details = release.Items.FirstOrDefault();
        if (details is null) return ("windows:unknown", new(BaselineOutcome.Unknown));
        var role = MachineRole.Read();
        if (role.PermissionDenied) return ($"windows:{details.Build}", new(BaselineOutcome.PermissionDenied));
        return ($"windows:{details.Build}", Read(details.Build, details.InstallationType, role.Role, path, valueName, expected));
    }

    internal static BaselineObservation Read(int? build, string? installationType, int? domainRole)
        => Read(build, installationType, domainRole, Path, ValueName, 1);

    private static BaselineObservation Read(int? build, string? installationType, int? domainRole, string path, string valueName, uint expected)
    {
        if (build is null || installationType is null || domainRole is null) return new(BaselineOutcome.Unknown);
        if (build != 20348 || !installationType.Contains("Server", StringComparison.OrdinalIgnoreCase) || domainRole != 3)
            return new(BaselineOutcome.NotApplicable);
        if (!Environment.Is64BitOperatingSystem) return new(BaselineOutcome.Unknown);
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return ReadValue(root, path, valueName, expected);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return new(BaselineOutcome.PermissionDenied); }
        catch (IOException) { return new(BaselineOutcome.Error); }
    }

    internal static BaselineObservation ReadValue(RegistryKey root, string path)
        => ReadValue(root, path, ValueName, 1);

    internal static BaselineObservation ReadValue(RegistryKey root, string path, string valueName, uint expected)
    {
        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            if (key is null) return new(BaselineOutcome.Fail, $"Registry DWORD is missing or differs from {expected}");
            RegistryValueKind kind;
            try { kind = key.GetValueKind(valueName); }
            catch (IOException ex) when (ex.HResult is 2 or unchecked((int)0x80070002))
            { return new(BaselineOutcome.Fail, $"Registry DWORD is missing or differs from {expected}"); }
            if (kind != RegistryValueKind.DWord) return new(BaselineOutcome.Fail, $"Registry DWORD is missing or differs from {expected}");
            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is int number && unchecked((uint)number) == expected
                ? new(BaselineOutcome.Pass, $"Registry DWORD is {expected}")
                : new(BaselineOutcome.Fail, $"Registry DWORD is missing or differs from {expected}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException) { return new(BaselineOutcome.PermissionDenied); }
        catch (IOException) { return new(BaselineOutcome.Error); }
    }
}

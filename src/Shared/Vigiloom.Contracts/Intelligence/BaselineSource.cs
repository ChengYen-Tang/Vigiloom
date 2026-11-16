namespace Vigiloom.Contracts.Intelligence;

public static class BaselineSources
{
    public const string LinuxShadow = "compliance-as-code-shadow";
    public const string WindowsNetworkSelection = "powerstig-network-selection";
    public const string LinuxShadowCheck = "file_permissions_etc_shadow";
    public const string WindowsNetworkSelectionCheck = "V-254348";
    public const string LinuxShadowOperation = "file-permissions-no-extended-acl";
    public const string WindowsRegistryOperation = "registry-dword-64";
    public const string LinuxShadowTarget = "/etc/shadow";
    public const string WindowsNetworkSelectionTarget = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System\DontDisplayNetworkSelectionUI";
}

/// <summary>A reviewed, read-only baseline check selected by the central platform.</summary>
public sealed record BaselineCheck(string Id, string Title, string Platform, string[] Versions,
    string Operation, string Target, string Expected, string SourceUrl, string License);

public enum BaselineSourceAvailability { Ready, Disabled, Stale, NoActiveRevision, Unavailable }

/// <summary>Revision and activation identify the exact centrally selected rule data.</summary>
public sealed record BaselineSourceSnapshot(BaselineSourceAvailability Availability, Guid? SourceId,
    Guid? RevisionId, string? ActivationVersion, string? UpstreamVersion, BaselineCheck? Check);

public interface IBaselineSource
{
    Task<BaselineSourceSnapshot> GetActiveAsync(string sourceKey, CancellationToken cancellationToken);
}

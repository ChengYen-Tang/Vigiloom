namespace Vigiloom.Contracts.Workspace;

/// <summary>Shared policy and built-in human role names; server authorization remains authoritative.</summary>
public static class WorkspaceAccess
{
    public const string MonitoringRead = "MonitoringRead";
    public const string Administrator = "Administrator";
    public const string ReadOnly = "Read only";
}

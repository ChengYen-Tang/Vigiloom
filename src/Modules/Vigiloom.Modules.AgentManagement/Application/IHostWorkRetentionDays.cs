namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Reads the platform's configured background-work retention period.</summary>
public interface IHostWorkRetentionDays
{
    Task<int> GetAsync(CancellationToken token);
}

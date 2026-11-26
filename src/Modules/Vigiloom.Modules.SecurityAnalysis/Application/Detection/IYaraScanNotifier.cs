using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

public interface IYaraScanNotifier
{
    Task NotifyAsync(SecurityAnalysisDbContext db, Guid agentId, CancellationToken token);
}

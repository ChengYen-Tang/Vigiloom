using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

public interface IYaraTimelineWriter
{
    Task RecordAsync(SecurityAnalysisDbContext db, Guid scanId, Guid deviceId, string path, int outcome, DateTimeOffset completedAt, CancellationToken token);
}

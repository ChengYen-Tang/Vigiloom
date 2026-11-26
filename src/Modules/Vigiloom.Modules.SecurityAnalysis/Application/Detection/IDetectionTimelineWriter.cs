using Vigiloom.Contracts.Events;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

public interface IDetectionTimelineWriter
{
    Task WriteAsync(SecurityAnalysisDbContext db, IReadOnlyList<EventTimelineEntry> entries, CancellationToken token);
}

using Vigiloom.Contracts.Events;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

internal sealed class RecordingDetectionTimeline : IDetectionTimelineWriter
{
    public List<EventTimelineEntry> Entries { get; } = [];
    public Task WriteAsync(SecurityAnalysisDbContext db, IReadOnlyList<EventTimelineEntry> entries, CancellationToken token)
    {
        Entries.AddRange(entries);
        return Task.CompletedTask;
    }
}

internal sealed class FailingDetectionTimeline : IDetectionTimelineWriter
{
    public Task WriteAsync(SecurityAnalysisDbContext db, IReadOnlyList<EventTimelineEntry> entries, CancellationToken token) =>
        throw new InvalidOperationException("Simulated timeline failure.");
}

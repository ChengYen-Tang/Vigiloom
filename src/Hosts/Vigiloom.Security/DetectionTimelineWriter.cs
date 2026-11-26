using System.Text.Json;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Security;

internal sealed class DetectionTimelineWriter(TemporalWorkWriter writer, IConfiguration configuration) : IDetectionTimelineWriter
{
    public async Task WriteAsync(SecurityAnalysisDbContext db, IReadOnlyList<EventTimelineEntry> entries, CancellationToken token)
    {
        foreach (var batch in entries.Chunk(2))
        {
            var deliveries = batch.Select(entry => new EventTimelineDelivery(Guid.CreateVersion7(), entry)).ToArray();
            var work = new ServiceWork(EventTimelineWork.BatchKind, JsonSerializer.Serialize(new EventTimelineBatch(deliveries)));
            await writer.WriteAsync(db, configuration["Work:OperationsQueue"] ?? OperationsWork.Queue, work, token);
        }
    }
}

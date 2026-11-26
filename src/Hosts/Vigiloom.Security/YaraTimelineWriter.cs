using System.Text.Json;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Security;

internal sealed class YaraTimelineWriter(TemporalWorkWriter writer, IConfiguration configuration) : IYaraTimelineWriter
{
    public async Task RecordAsync(SecurityAnalysisDbContext db, Guid scanId, Guid deviceId, string path, int outcome, DateTimeOffset completedAt, CancellationToken token)
    {
        var entry = new EventTimelineEntry("yara", completedAt, deviceId, "device", $"YARA 掃描：{path}",
            $"掃描 {scanId:D}；結果 {outcome}", $"/devices/{deviceId:D}/yara-scans?scan={scanId:D}");
        var work = new ServiceWork(EventTimelineWork.Kind, JsonSerializer.Serialize(new EventTimelineDelivery(scanId, entry)));
        await writer.WriteAsync(db, configuration["Work:OperationsQueue"] ?? OperationsWork.Queue, work, token);
    }
}

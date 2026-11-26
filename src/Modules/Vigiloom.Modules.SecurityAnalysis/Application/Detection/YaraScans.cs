using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Modules.SecurityAnalysis.Domain;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Manages security-owned YARA work for an Agent verified by Resources.</summary>
public sealed class YaraScans(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    public async Task<YaraScanJob> CreateAsync(RegisteredAgentIdentity identity, YaraScanRequest request, Guid actor, CancellationToken token)
    {
        if (identity.AgentId.Version != 7 || identity.DeviceId.Version != 7 || actor.Version != 7 || !Valid(request))
            throw new ArgumentException("Invalid scan request.");
        var scan = new YaraScan { Id = Guid.CreateVersion7(), AgentId = identity.AgentId, DeviceId = identity.DeviceId,
            RequestedBy = actor, FilePath = request.FilePath, Rules = request.Rules, MaximumBytes = request.MaximumBytes,
            TimeoutSeconds = request.TimeoutSeconds, RequestedAt = clock.GetUtcNow() };
        await using var db = await contexts.CreateDbContextAsync(token);
        db.YaraScans.Add(scan);
        await db.SaveChangesAsync(token);
        return View(scan);
    }

    public async Task<IReadOnlyList<YaraScanJob>> ListAsync(Guid deviceId, CancellationToken token)
    {
        if (deviceId.Version != 7) throw new ArgumentException("Invalid device.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.YaraScans.AsNoTracking().Where(x => x.DeviceId == deviceId)
            .OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.RowId).Take(100).ToArrayAsync(token);
        return rows.Select(View).ToArray();
    }

    public async Task<YaraScanJob?> NextAsync(RegisteredAgentIdentity identity, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.YaraScans.AsNoTracking().Where(x => x.AgentId == identity.AgentId && x.DeviceId == identity.DeviceId && x.Outcome == null)
            .OrderBy(x => x.RequestedAt).ThenBy(x => x.RowId).FirstOrDefaultAsync(token);
        return row is null ? null : View(row);
    }

    public async Task<YaraScanJob?> GetAsync(Guid deviceId, Guid scanId, CancellationToken token)
    {
        if (deviceId.Version != 7 || scanId.Version != 7) throw new ArgumentException("Invalid scan.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.YaraScans.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.Id == scanId, token);
        return row is null ? null : View(row);
    }

    public async Task<bool> CancelAsync(Guid deviceId, Guid scanId, CancellationToken token)
    {
        if (deviceId.Version != 7 || scanId.Version != 7) throw new ArgumentException("Invalid scan.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = clock.GetUtcNow();
        return await db.YaraScans.Where(x => x.Id == scanId && x.DeviceId == deviceId && x.Outcome == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Outcome, (int)YaraScanOutcome.Cancelled)
                .SetProperty(x => x.CompletedAt, now).SetProperty(x => x.Output, "").SetProperty(x => x.Error, "")
                .SetProperty(x => x.OutputTruncated, false), token) == 1;
    }

    private static bool Valid(YaraScanRequest? request) => request is not null &&
        !string.IsNullOrWhiteSpace(request.FilePath) && request.FilePath.Length <= 1024 &&
        !string.IsNullOrWhiteSpace(request.Rules) && request.Rules.Length <= 65536 &&
        request.MaximumBytes is > 0 and <= 1073741824 && request.TimeoutSeconds is >= 1 and <= 3600;

    private static YaraScanJob View(YaraScan scan) => new(scan.Id, scan.AgentId, scan.DeviceId, scan.RequestedBy,
        new(scan.FilePath, scan.Rules, scan.MaximumBytes, scan.TimeoutSeconds), scan.RequestedAt,
        scan.Outcome is { } result ? new((YaraScanOutcome)result, scan.Output ?? "", scan.Error ?? "", scan.OutputTruncated ?? false, scan.CompletedAt!.Value, scan.EngineVersion) : null);
}

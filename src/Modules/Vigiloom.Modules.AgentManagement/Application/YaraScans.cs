using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Queues single-file scans for an enrolled Agent and records their results.</summary>
public sealed class YaraScans(IDbContextFactory<AgentManagementDbContext> contexts, AgentIdentity identity,
    IDeviceAgentSource devices, TimeProvider clock)
{
    public async Task<YaraScanJob?> CreateAsync(Guid deviceId, YaraScanRequest request, Guid actor, CancellationToken token)
    {
        if (deviceId.Version != 7 || actor.Version != 7 || !Valid(request)) throw new ArgumentException("Invalid scan request.");
        var device = await devices.GetAsync(deviceId, token);
        if (device is null || !device.Eligible) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.DeviceGeneration == device.Generation && x.RevokedAt == null, token);
        if (agent is null) return null;
        var scan = new YaraScan { Id = Guid.CreateVersion7(), AgentId = agent.Id, DeviceId = deviceId, RequestedBy = actor,
            FilePath = request.FilePath, Rules = request.Rules, MaximumBytes = request.MaximumBytes,
            TimeoutSeconds = request.TimeoutSeconds, RequestedAt = clock.GetUtcNow() };
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

    public async Task<(bool Authenticated, YaraScanJob? Job)> NextAsync(string? credential, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await identity.AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return (false, null);
        var row = await db.YaraScans.AsNoTracking().Where(x => x.AgentId == agent.Id && x.Outcome == null)
            .OrderBy(x => x.RequestedAt).ThenBy(x => x.RowId).FirstOrDefaultAsync(token);
        await transaction.CommitAsync(token);
        return (true, row is null ? null : View(row));
    }

    private static bool Valid(YaraScanRequest? request) => request is not null &&
        !string.IsNullOrWhiteSpace(request.FilePath) && request.FilePath.Length <= 1024 &&
        !string.IsNullOrWhiteSpace(request.Rules) && request.Rules.Length <= 65536 &&
        request.MaximumBytes is > 0 and <= 1073741824 && request.TimeoutSeconds is >= 1 and <= 3600;

    private static YaraScanJob View(YaraScan scan) => new(scan.Id, scan.AgentId, scan.DeviceId, scan.RequestedBy,
        new(scan.FilePath, scan.Rules, scan.MaximumBytes, scan.TimeoutSeconds), scan.RequestedAt,
        scan.Outcome is { } result ? new((YaraScanOutcome)result, scan.Output ?? "", scan.Error ?? "", scan.OutputTruncated ?? false, scan.CompletedAt!.Value) : null);
}

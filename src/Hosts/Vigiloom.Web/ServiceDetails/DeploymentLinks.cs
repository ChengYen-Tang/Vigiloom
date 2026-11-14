using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.ServiceDetails;

internal enum DeploymentLinkStatus { Saved, NotFound, Conflict, Invalid }
internal sealed record DeploymentLinkResult(DeploymentLinkStatus Status, Guid? Id = null);

/// <summary>Coordinates an administrator's attestation across independently owned resources.</summary>
internal sealed class DeploymentLinks(IDbContextFactory<DeploymentLinksDbContext> links,
    IDbContextFactory<ResourcesDbContext> resources, IDbContextFactory<ServiceObservabilityDbContext> services,
    IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<DeploymentLinkPage?> ListAsync(Guid serviceId, int page, CancellationToken token)
    {
        if (serviceId.Version != 7 || page is < 1 or > 1000) throw new ArgumentException("Invalid service or page.");
        await using var serviceDb = await services.CreateDbContextAsync(token);
        if (!await serviceDb.Services.AsNoTracking().AnyAsync(x => x.Id == serviceId, token)) return null;
        await using var db = await links.CreateDbContextAsync(token);
        var rows = await db.Database.SqlQuery<LinkRow>($"""
            SELECT l.[Id], l.[DeviceId], d.[Name] AS [DeviceName], l.[ServiceInstanceId], i.[ExternalInstanceId] AS [InstanceName], l.[Evidence], l.[StartedAt], l.[Version], l.[RowId],
                d.[IsArchived] AS [DeviceArchived], CAST(CASE WHEN i.[RetiredAt] IS NULL THEN 0 ELSE 1 END AS bit) AS [InstanceRetired],
                CAST(CASE WHEN src.[RevokedAt] IS NULL THEN 0 ELSE 1 END AS bit) AS [SourceRevoked],
                CAST(CASE WHEN s.[RevokedAt] IS NULL THEN 0 ELSE 1 END AS bit) AS [ServiceStopped]
            FROM [resources].[DeploymentLinks] AS l
            JOIN [resources].[Devices] AS d ON d.[Id] = l.[DeviceId]
            JOIN [service].[Instances] AS i ON i.[Id] = l.[ServiceInstanceId]
            JOIN [service].[Services] AS s ON s.[Id] = i.[ServiceId]
            JOIN [service].[Sources] AS src ON src.[Id] = s.[SourceId]
            WHERE i.[ServiceId] = {serviceId} AND l.[EndedAt] IS NULL
            """).OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(51).ToArrayAsync(token);
        return new(rows.Take(50).Select(x => new DeploymentLinkView(x.Id, x.DeviceId, x.DeviceName, x.ServiceInstanceId, x.InstanceName,
            x.Evidence, x.StartedAt, Convert.ToBase64String(x.Version), x.DeviceArchived, x.InstanceRetired, x.SourceRevoked, x.ServiceStopped)).ToArray(), page < 1000 && rows.Length > 50);
    }

    public async Task<DeploymentLinkResult> CreateAsync(Guid serviceId, CreateDeploymentLink input, Guid actor, CancellationToken token)
    {
        if (serviceId.Version != 7 || actor.Version != 7 || input.DeviceId.Version != 7 || input.ServiceInstanceId.Version != 7 ||
            string.IsNullOrWhiteSpace(input.Evidence) || input.Evidence.Trim().Length > 512)
            return new(DeploymentLinkStatus.Invalid);
        await using var db = await links.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await using var serviceDb = await services.CreateDbContextAsync(token);
        serviceDb.Database.SetDbConnection(db.Database.GetDbConnection());
        await serviceDb.Database.UseTransactionAsync(tx.GetDbTransaction(), token);
        var sourceId = await serviceDb.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return new(DeploymentLinkStatus.NotFound);
        var source = await serviceDb.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId.Value}").SingleOrDefaultAsync(token);
        var service = await serviceDb.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}").SingleOrDefaultAsync(token);
        if (source is null || service is null) return new(DeploymentLinkStatus.NotFound);
        if (source.RevokedAt is not null || service.RevokedAt is not null) return new(DeploymentLinkStatus.Conflict);
        var instance = await serviceDb.Instances.FromSqlInterpolated($"SELECT * FROM [service].[Instances] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {input.ServiceInstanceId}").SingleOrDefaultAsync(token);
        if (instance is null || instance.ServiceId != serviceId) return new(DeploymentLinkStatus.NotFound);
        if (instance.RetiredAt is not null) return new(DeploymentLinkStatus.Conflict);
        await using var resourceDb = await resources.CreateDbContextAsync(token);
        resourceDb.Database.SetDbConnection(db.Database.GetDbConnection());
        await resourceDb.Database.UseTransactionAsync(tx.GetDbTransaction(), token);
        var device = await resourceDb.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {input.DeviceId}").SingleOrDefaultAsync(token);
        if (device is null) return new(DeploymentLinkStatus.NotFound);
        if (device.IsArchived) return new(DeploymentLinkStatus.Conflict);
        var now = await DatabaseTimeAsync(db, token);
        var lastEnd = await db.Links.Where(x => x.DeviceId == input.DeviceId && x.ServiceInstanceId == input.ServiceInstanceId)
            .MaxAsync(x => x.EndedAt, token);
        if (lastEnd > now) return new(DeploymentLinkStatus.Conflict);
        var link = new DeploymentLink(input.DeviceId, input.ServiceInstanceId, actor, input.Evidence, now);
        db.Links.Add(link);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(DeploymentLinkStatus.Conflict); }
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, link.Id, now, new DeploymentLinkChanged(DeploymentLinkAction.Created, link.DeviceId, link.ServiceInstanceId)), token);
        await tx.CommitAsync(token);
        return new(DeploymentLinkStatus.Saved, link.Id);
    }

    public async Task<DeploymentLinkStatus> EndAsync(Guid serviceId, Guid id, EndDeploymentLink input, Guid actor, CancellationToken token)
    {
        if (serviceId.Version != 7 || id.Version != 7 || actor.Version != 7) return DeploymentLinkStatus.Invalid;
        byte[] expected;
        try { expected = Convert.FromBase64String(input.Version ?? ""); }
        catch (FormatException) { return DeploymentLinkStatus.Invalid; }
        if (expected.Length != 8) return DeploymentLinkStatus.Invalid;
        await using var db = await links.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var identity = await db.Links.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.DeviceId, x.ServiceInstanceId }).SingleOrDefaultAsync(token);
        if (identity is null) return DeploymentLinkStatus.NotFound;
        await using var serviceDb = await services.CreateDbContextAsync(token);
        serviceDb.Database.SetDbConnection(db.Database.GetDbConnection());
        await serviceDb.Database.UseTransactionAsync(tx.GetDbTransaction(), token);
        var sourceId = await serviceDb.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return DeploymentLinkStatus.NotFound;
        var source = await serviceDb.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId.Value}").SingleOrDefaultAsync(token);
        var service = await serviceDb.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}").SingleOrDefaultAsync(token);
        var instance = await serviceDb.Instances.FromSqlInterpolated($"SELECT * FROM [service].[Instances] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {identity.ServiceInstanceId}").SingleOrDefaultAsync(token);
        if (source is null || service is null || instance is null || instance.ServiceId != serviceId) return DeploymentLinkStatus.NotFound;
        await using var resourceDb = await resources.CreateDbContextAsync(token);
        resourceDb.Database.SetDbConnection(db.Database.GetDbConnection());
        await resourceDb.Database.UseTransactionAsync(tx.GetDbTransaction(), token);
        var device = await resourceDb.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {identity.DeviceId}").SingleOrDefaultAsync(token);
        if (device is null) return DeploymentLinkStatus.NotFound;
        var link = await db.Links.FromSqlInterpolated($"SELECT * FROM [resources].[DeploymentLinks] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (link is null) return DeploymentLinkStatus.NotFound;
        if (link.EndedAt is not null || !expected.AsSpan().SequenceEqual(link.Version)) return DeploymentLinkStatus.Conflict;
        var now = await DatabaseTimeAsync(db, token);
        if (now < link.StartedAt) return DeploymentLinkStatus.Conflict;
        link.End(now);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, link.Id, now, new DeploymentLinkChanged(DeploymentLinkAction.Ended, link.DeviceId, link.ServiceInstanceId)), token);
        await tx.CommitAsync(token);
        return DeploymentLinkStatus.Saved;
    }

    private static Task<DateTimeOffset> DatabaseTimeAsync(DeploymentLinksDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);

    private sealed class LinkRow
    {
        public Guid Id { get; set; }
        public Guid DeviceId { get; set; }
        public string DeviceName { get; set; } = "";
        public Guid ServiceInstanceId { get; set; }
        public string InstanceName { get; set; } = "";
        public string Evidence { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public byte[] Version { get; set; } = [];
        public long RowId { get; set; }
        public bool DeviceArchived { get; set; }
        public bool InstanceRetired { get; set; }
        public bool SourceRevoked { get; set; }
        public bool ServiceStopped { get; set; }
    }
}

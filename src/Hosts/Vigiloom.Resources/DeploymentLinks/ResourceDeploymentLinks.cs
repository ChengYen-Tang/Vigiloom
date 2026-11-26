using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Resources.DeploymentLinks;

/// <summary>Manages resource-owned deployment links using instance information from observability.</summary>
internal sealed class ResourceDeploymentLinks(IDbContextFactory<ResourceDeploymentLinksDbContext> links,
    IDbContextFactory<ResourcesDbContext> resources, HttpClient observability,
    IApplicationSourceVerifier sources, TemporalAuditWriter audit)
{
    public async Task<DeploymentLinkPage?> ListAsync(Guid serviceId, int page, CancellationToken token)
    {
        if (serviceId.Version != 7 || page is < 1 or > 1000) throw new ArgumentException("Invalid service or page.");
        await using var db = await resources.CreateDbContextAsync(token);
        var service = await db.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serviceId, token);
        if (service is null) return null;
        var ids = await observability.GetFromJsonAsync<Guid[]>($"internal/v1/instance-admin/services/{serviceId:D}/ids", token)
            ?? throw new InvalidOperationException("Instance identifiers are unavailable.");
        await using var store = await links.CreateDbContextAsync(token);
        var rows = await store.Links.AsNoTracking().Where(x => ids.Contains(x.ServiceInstanceId) && x.EndedAt == null)
            .OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(51).ToArrayAsync(token);
        var shown = rows.Take(50).ToArray();
        var deviceIds = shown.Select(x => x.DeviceId).Distinct().ToArray();
        var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        using var response = await observability.PostAsJsonAsync($"internal/v1/instance-admin/services/{serviceId:D}/lookup", shown.Select(x => x.ServiceInstanceId).Distinct().ToArray(), token);
        response.EnsureSuccessStatusCode();
        var instances = (await response.Content.ReadFromJsonAsync<ServiceInstanceSummary[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Instance details are unavailable.")).ToDictionary(x => x.Id);
        var source = await sources.GetAsync(service.SourceId, token);
        return new(shown.Select(x => new DeploymentLinkView(x.Id, x.DeviceId, devices[x.DeviceId].Name,
            x.ServiceInstanceId, instances[x.ServiceInstanceId].ExternalInstanceId, x.Evidence, x.StartedAt,
            Convert.ToBase64String(x.Version), devices[x.DeviceId].IsArchived, instances[x.ServiceInstanceId].RetiredAt != null,
            !source.Active, service.RevokedAt != null)).ToArray(), rows.Length > 50 && page < 1000);
    }

    public async Task<DeploymentLinkMutationResult> CreateAsync(Guid serviceId, CreateDeploymentLinkCommand command, CancellationToken token)
    {
        var input = command.Input;
        if (serviceId.Version != 7 || command.ActorId.Version != 7 || input.DeviceId.Version != 7 || input.ServiceInstanceId.Version != 7 ||
            string.IsNullOrWhiteSpace(input.Evidence) || input.Evidence.Trim().Length > 512) return new(DeploymentLinkMutationStatus.Invalid);
        await using var resourcesDb = await resources.CreateDbContextAsync(token);
        var service = await resourcesDb.ApplicationServices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serviceId, token);
        if (service is null) return new(DeploymentLinkMutationStatus.NotFound);
        if (service.RevokedAt != null || !(await sources.GetAsync(service.SourceId, token)).Active) return new(DeploymentLinkMutationStatus.Conflict);
        using var response = await observability.PostAsJsonAsync($"internal/v1/instance-admin/services/{serviceId:D}/lookup", new[] { input.ServiceInstanceId }, token);
        response.EnsureSuccessStatusCode();
        var instance = (await response.Content.ReadFromJsonAsync<ServiceInstanceSummary[]>(cancellationToken: token))?.SingleOrDefault();
        if (instance is null) return new(DeploymentLinkMutationStatus.NotFound);
        if (instance.RetiredAt != null) return new(DeploymentLinkMutationStatus.Conflict);
        await using var db = await links.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        resourcesDb.Database.SetDbConnection(db.Database.GetDbConnection());
        await resourcesDb.Database.UseTransactionAsync(tx.GetDbTransaction(), token);
        var currentService = await resourcesDb.ApplicationServices.FromSqlInterpolated($"SELECT * FROM [resources].[ApplicationServices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}").SingleOrDefaultAsync(token);
        if (currentService is null) return new(DeploymentLinkMutationStatus.NotFound);
        if (currentService.RevokedAt != null || currentService.ArchivedAt != null) return new(DeploymentLinkMutationStatus.Conflict);
        var device = await resourcesDb.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {input.DeviceId}").SingleOrDefaultAsync(token);
        if (device is null) return new(DeploymentLinkMutationStatus.NotFound);
        if (device.IsArchived) return new(DeploymentLinkMutationStatus.Conflict);
        var now = await NowAsync(db, token);
        if (await db.Links.Where(x => x.DeviceId == input.DeviceId && x.ServiceInstanceId == input.ServiceInstanceId).MaxAsync(x => x.EndedAt, token) > now)
            return new(DeploymentLinkMutationStatus.Conflict);
        var link = new DeploymentLink(input.DeviceId, input.ServiceInstanceId, command.ActorId, input.Evidence, now);
        db.Links.Add(link);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(DeploymentLinkMutationStatus.Conflict); }
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(command.ActorId, link.Id, now, new DeploymentLinkChanged(DeploymentLinkAction.Created, link.DeviceId, link.ServiceInstanceId)), token);
        await tx.CommitAsync(token);
        return new(DeploymentLinkMutationStatus.Saved, link.Id);
    }

    public async Task<DeploymentLinkMutationStatus> EndAsync(Guid serviceId, Guid id, EndDeploymentLinkCommand command, CancellationToken token)
    {
        if (serviceId.Version != 7 || id.Version != 7 || command.ActorId.Version != 7) return DeploymentLinkMutationStatus.Invalid;
        byte[] expected;
        try { expected = Convert.FromBase64String(command.Input.Version ?? ""); }
        catch (FormatException) { return DeploymentLinkMutationStatus.Invalid; }
        if (expected.Length != 8) return DeploymentLinkMutationStatus.Invalid;
        await using var db = await links.CreateDbContextAsync(token);
        var identity = await db.Links.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.ServiceInstanceId).SingleOrDefaultAsync(token);
        if (identity is null) return DeploymentLinkMutationStatus.NotFound;
        using var response = await observability.PostAsJsonAsync($"internal/v1/instance-admin/services/{serviceId:D}/lookup", new[] { identity.Value }, token);
        response.EnsureSuccessStatusCode();
        if ((await response.Content.ReadFromJsonAsync<ServiceInstanceSummary[]>(cancellationToken: token))?.Length != 1)
            return DeploymentLinkMutationStatus.NotFound;
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var link = await db.Links.FromSqlInterpolated($"SELECT * FROM [resources].[DeploymentLinks] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);
        if (link is null) return DeploymentLinkMutationStatus.NotFound;
        if (link.EndedAt != null || !expected.AsSpan().SequenceEqual(link.Version)) return DeploymentLinkMutationStatus.Conflict;
        var now = await NowAsync(db, token);
        if (now < link.StartedAt) return DeploymentLinkMutationStatus.Conflict;
        link.End(now);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(command.ActorId, link.Id, now, new DeploymentLinkChanged(DeploymentLinkAction.Ended, link.DeviceId, link.ServiceInstanceId)), token);
        await tx.CommitAsync(token);
        return DeploymentLinkMutationStatus.Saved;
    }

    private static Task<DateTimeOffset> NowAsync(ResourceDeploymentLinksDbContext db, CancellationToken token) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
}

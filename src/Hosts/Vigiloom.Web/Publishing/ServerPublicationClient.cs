using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Web.Api;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Publishing;

internal sealed class ServerPublicationClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    PublicationAdministration administration, PublicationPreviewReader preview, PublicDeviceHistoryReader history, TemplateReader templates,
    TemplateAdministration templateAdministration, TemplateUpdateWorkflow updates, TemplateRetirementWorkflow retirements) : IPublicationClient
{
    public async Task<PublicationSettings?> GetAsync(string kind, Guid id, CancellationToken token)
    {
        await ActorAsync(token);
        return PublicationEndpoints.TryKind(kind, out var type) ? await administration.ReadAsync(type, id, token) : null;
    }

    public async Task<PublicationPreview?> PreviewAsync(string kind, Guid id, CancellationToken token)
    {
        await ActorAsync(token);
        return PublicationEndpoints.TryKind(kind, out var type) ? await preview.ReadAsync(type, id, token) : null;
    }

    public async Task<PublicDeviceHistory?> PublishedDeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        await ActorAsync(token);
        return await history.ReadAsync(id, from, to, token);
    }

    public async Task<PublicationTemplatePage> TemplatesAsync(int page, CancellationToken token)
    { await ActorAsync(token); return await templates.ListAsync(page, token); }
    public async Task<PublicationTemplateItem?> TemplateAsync(Guid id, CancellationToken token)
    { await ActorAsync(token); return await templates.GetAsync(id, token); }
    public async Task<PublicationTemplateItem?> DefaultTemplateAsync(CancellationToken token)
    { await ActorAsync(token); return await templates.DefaultAsync(token); }
    public async Task<TemplateOperationResult<PublicationImpact>> TemplateImpactAsync(Guid id, int page, CancellationToken token)
    {
        await ActorAsync(token);
        if (page is < 1 or > (TemplateImpact.MaximumDevices + TemplateImpact.PageSize - 1) / TemplateImpact.PageSize) return new(TemplateOperationStatus.Invalid);
        var result = await templates.ImpactAsync(id, page, token);
        return result.Changed || result.TooMany ? new(TemplateOperationStatus.Conflict) : result.Impact is null ? new(TemplateOperationStatus.NotFound) : new(TemplateOperationStatus.Success, result.Impact);
    }

    public async Task<TemplateOperationResult<PublicationTemplateItem>> CreateTemplateAsync(CreatePublicationTemplate input, CancellationToken token)
    { var result = await templateAdministration.CreateAsync(input, await ActorAsync(token), token); return new(Status(result.Status), result.Template); }
    public async Task<TemplateOperationResult<PublicationTemplateItem>> SetDefaultTemplateAsync(Guid id, SetDefaultPublicationTemplate input, CancellationToken token)
    { var result = await templateAdministration.SetDefaultAsync(id, input, await ActorAsync(token), token); return new(Status(result.Status), result.Template); }
    public async Task<TemplateOperationResult<PublicationTemplateUpdatePreview>> PreviewTemplateUpdateAsync(Guid id, PublicationTemplateUpdate input, CancellationToken token)
    { var result = await updates.PreviewAsync(id, input, await ActorAsync(token), token); return new(Status(result.Status), result.Preview); }
    public async Task<TemplateOperationResult<PublicationTemplateItem>> ConfirmTemplateUpdateAsync(Guid id, ConfirmPublicationTemplateUpdate input, CancellationToken token)
    { var result = await updates.ConfirmAsync(id, input, await ActorAsync(token), token); return new(Status(result.Status), result.Template); }
    public async Task<TemplateOperationResult<PublicationTemplateRetirementPreview>> PreviewTemplateRetirementAsync(Guid id, PublicationTemplateRetirement input, CancellationToken token)
    { var result = await retirements.PreviewAsync(id, input, await ActorAsync(token), token); return new(Status(result.Status), result.Preview); }
    public async Task<TemplateOperationResult<bool>> ConfirmTemplateRetirementAsync(Guid id, ConfirmPublicationTemplateRetirement input, CancellationToken token)
    { var result = await retirements.ConfirmAsync(id, input, await ActorAsync(token), token); return new(Status(result), result == TemplateChangeStatus.Updated); }

    private static TemplateOperationStatus Status(TemplateChangeStatus status) => status switch
    { TemplateChangeStatus.Created or TemplateChangeStatus.Updated => TemplateOperationStatus.Success,
        TemplateChangeStatus.NotFound => TemplateOperationStatus.NotFound, TemplateChangeStatus.Invalid => TemplateOperationStatus.Invalid, _ => TemplateOperationStatus.Conflict };

    public async Task<PublicationSaveResult> SaveAsync(string kind, Guid id, PublicationChange change, CancellationToken token)
    {
        var actor = await ActorAsync(token);
        if (!PublicationEndpoints.TryKind(kind, out var type)) return new(PublicationSaveStatus.NotFound);
        var result = await administration.SetAsync(type, id, change, actor, token);
        return new(result.Status switch { PublicationChangeStatus.Updated => PublicationSaveStatus.Updated,
            PublicationChangeStatus.NotFound => PublicationSaveStatus.NotFound, PublicationChangeStatus.Invalid => PublicationSaveStatus.Invalid,
            _ => PublicationSaveStatus.Conflict }, result.Settings);
    }

    private async Task<Guid> ActorAsync(CancellationToken token)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        if (!SessionLifetime.IsCurrent(principal) || (await identities.GetAsync(principal, token)) is not { } identity ||
            !identity.Roles.Contains(WorkspaceAccess.Administrator) || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty)
            throw new UnauthorizedAccessException();
        return actor;
    }
}

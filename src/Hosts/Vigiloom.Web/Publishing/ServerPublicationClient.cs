using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Web.Api;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Publishing;

internal sealed class ServerPublicationClient(AuthenticationStateProvider authentication, WorkspaceIdentityService identities,
    PublicationAdministration administration, PublicationPreviewReader preview) : IPublicationClient
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

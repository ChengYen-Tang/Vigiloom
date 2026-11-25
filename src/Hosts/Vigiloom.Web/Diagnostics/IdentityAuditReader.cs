using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Diagnostics;

internal sealed class IdentityAuditReader(IServiceProvider services, IConfiguration configuration, IOptions<IdentityOptions> options)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]);
    private RemoteIdentityAuditReader Client => services.GetRequiredService<RemoteIdentityAuditReader>();
    public Task<AuditFeedPage> QueryAsync(ClaimsPrincipal principal, AuditFeedSource source, OperationAuditQuery query, CancellationToken token) =>
        Remote ? Client.QueryAsync(new(IdentityActorFactory.Create(principal, options.Value), source, query), token)
            : services.GetRequiredService<IdentityAuditFeed>().QueryAsync(source, query, token);
    public async Task<Dictionary<Guid, string>> NamesAsync(ClaimsPrincipal principal, Guid[] ids, CancellationToken token)
    {
        if (Remote) return await Client.NamesAsync(new(IdentityActorFactory.Create(principal, options.Value), ids), token);
        var users = services.GetRequiredService<UserManager<Account>>();
        return await users.Users.AsNoTracking().Where(user => ids.Contains(user.Id) && user.UserName != null)
            .ToDictionaryAsync(user => user.Id, user => user.UserName!, token);
    }
}

internal sealed class RemoteIdentityAuditReader(HttpClient client)
{
    public Task<AuditFeedPage> QueryAsync(IdentityAuditQueryRequest request, CancellationToken token) => SendAsync<IdentityAuditQueryRequest, AuditFeedPage>("query", request, token);
    public Task<Dictionary<Guid, string>> NamesAsync(IdentityAuditNamesRequest request, CancellationToken token) => SendAsync<IdentityAuditNamesRequest, Dictionary<Guid, string>>("names", request, token);
    private async Task<TResponse> SendAsync<TRequest, TResponse>(string action, TRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/audit/" + action, request, token);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty audit response.");
    }
}

using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Web.Groups;

internal sealed class RemoteServiceMembership(HttpClient client) : IServiceMembership
{
    private const string Root = "internal/v1/group-management";
    public Task<ServiceGroupLinkPage> ListGroupsAsync(Guid serviceId, int page, CancellationToken token) =>
        GetAsync<ServiceGroupLinkPage>($"{Root}/service/{serviceId:D}?page={page}", token);

    public Task<GroupServicePage> ListAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token) =>
        GetAsync<GroupServicePage>(QueryHelpers.AddQueryString($"{Root}/services", new Dictionary<string, string?>
        {
            ["groupId"] = groupId?.ToString("D"), ["ungrouped"] = ungrouped.ToString(), ["member"] = member?.ToString(),
            ["search"] = search, ["page"] = page.ToString()
        }), token);

    public async Task<GroupResult> ChangeAsync(Guid id, GroupMembershipChange change, Guid actor, CancellationToken token)
    {
        using var response = await client.PutAsJsonAsync($"{Root}/{id:D}/services", new GroupMembersRequest(change, actor), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GroupResult>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty response.");
    }

    private async Task<T> GetAsync<T>(string uri, CancellationToken token) =>
        await client.GetFromJsonAsync<T>(uri, token) ?? throw new InvalidOperationException("Resource service returned an empty response.");
}

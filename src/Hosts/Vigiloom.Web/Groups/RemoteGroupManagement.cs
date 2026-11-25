using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Groups;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Web.Groups;

internal sealed class RemoteGroupManagement(HttpClient client) : IGroupManagement
{
    private const string Root = "internal/v1/group-management";
    public Task<GroupPage> ListAsync(string? search, int page, CancellationToken token) =>
        GetAsync<GroupPage>(QueryHelpers.AddQueryString(Root + "/", new Dictionary<string, string?> { ["search"] = search, ["page"] = page.ToString() }), token);

    public async Task<GroupDetails?> GetAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"{Root}/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<GroupDetails>(response, token);
    }

    public Task<DeviceGroupPage> ListForDeviceAsync(Guid deviceId, int page, CancellationToken token) =>
        GetAsync<DeviceGroupPage>($"{Root}/device/{deviceId:D}?page={page}", token);

    public Task<GroupDevicePage> ListDevicesAsync(Guid? groupId, bool ungrouped, bool? member, string? search, int page, CancellationToken token) =>
        GetAsync<GroupDevicePage>(QueryHelpers.AddQueryString($"{Root}/devices", new Dictionary<string, string?>
        {
            ["groupId"] = groupId?.ToString("D"), ["ungrouped"] = ungrouped.ToString(), ["member"] = member?.ToString(),
            ["search"] = search, ["page"] = page.ToString()
        }), token);

    public Task<GroupResult> CreateAsync(GroupInput input, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Post, Root + "/", new GroupWriteRequest(input, actor), token);
    public Task<GroupResult> UpdateAsync(Guid id, GroupInput input, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Put, $"{Root}/{id:D}", new GroupWriteRequest(input, actor), token);
    public Task<GroupResult> ChangeMembersAsync(Guid id, GroupMembershipChange change, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Put, $"{Root}/{id:D}/devices", new GroupMembersRequest(change, actor), token);
    public Task<GroupResult> DeleteAsync(Guid id, string? expectedVersion, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Post, $"{Root}/{id:D}/delete", new GroupDeleteRequest(expectedVersion, actor), token);

    private async Task<GroupResult> SendAsync<T>(HttpMethod method, string uri, T value, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(value) };
        using var response = await client.SendAsync(request, token);
        return await ReadAsync<GroupResult>(response, token);
    }

    private async Task<T> GetAsync<T>(string uri, CancellationToken token) =>
        await client.GetFromJsonAsync<T>(uri, token) ?? throw new InvalidOperationException("Resource service returned an empty response.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty response.");
    }
}

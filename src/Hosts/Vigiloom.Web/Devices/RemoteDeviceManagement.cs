using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Web.Devices;

internal sealed class RemoteDeviceManagement(HttpClient client) : IDeviceManagement
{
    private const string Root = "internal/v1/device-management";

    public Task<DevicePage> ListAsync(string? search, string? sort, bool descending, bool archived, int page, CancellationToken token) =>
        GetRequiredAsync<DevicePage>(QueryHelpers.AddQueryString(Root + "/", new Dictionary<string, string?>
        {
            ["search"] = search, ["sort"] = sort, ["descending"] = descending.ToString(),
            ["archived"] = archived.ToString(), ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }), token);

    public Task<DevicePage> ListForOverviewAsync(int page, CancellationToken token) =>
        GetRequiredAsync<DevicePage>($"{Root}/overview?page={page}", token);

    public async Task<DeviceDetails?> GetAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"{Root}/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<DeviceDetails>(response, token);
    }

    public Task<IReadOnlyList<DeviceDetails>> FindAddressMatchesAsync(string address, Guid? excludeId, CancellationToken token) =>
        FindAddressMatchesAsync([address], excludeId, token);

    public async Task<IReadOnlyList<DeviceDetails>> FindAddressMatchesAsync(IEnumerable<string> addresses, Guid? excludeId, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync($"{Root}/address-matches", new AddressMatchesRequest(addresses.ToArray(), excludeId), token);
        return await ReadAsync<DeviceDetails[]>(response, token);
    }

    public Task<DeviceResult> CreateAsync(DeviceInput input, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Post, Root + "/", new DeviceWriteRequest(input, actor), token);

    public Task<DeviceResult> UpdateAsync(Guid id, DeviceInput input, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Put, $"{Root}/{id:D}", new DeviceWriteRequest(input, actor), token);

    public Task<DeviceResult> SetArchivedAsync(Guid id, bool archive, string? expectedVersion, Guid actor, CancellationToken token) =>
        SendAsync(HttpMethod.Post, $"{Root}/{id:D}/archive", new DeviceArchiveRequest(archive, expectedVersion, actor), token);

    private async Task<DeviceResult> SendAsync<T>(HttpMethod method, string uri, T value, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(value) };
        using var response = await client.SendAsync(request, token);
        return await ReadAsync<DeviceResult>(response, token);
    }

    private async Task<T> GetRequiredAsync<T>(string uri, CancellationToken token) =>
        await client.GetFromJsonAsync<T>(uri, token) ?? throw new InvalidOperationException("Resource service returned an empty response.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Resource service returned an empty response.");
    }
}

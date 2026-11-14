using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserPublicStatusClient(HttpClient client) : IPublicStatusClient
{
    public Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken cancellationToken) =>
        ReadRequiredAsync<PublicPage<PublicGroup>>($"api/v1/public/groups{Query(search, page)}", cancellationToken);
    public Task<PublicGroup?> GroupAsync(Guid id, CancellationToken cancellationToken) => ReadAsync<PublicGroup>($"api/v1/public/groups/{id:D}", cancellationToken);
    public Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) =>
        ReadRequiredAsync<PublicPage<PublicDevice>>($"api/v1/public/{GroupPrefix(groupId)}devices{Query(search, page)}", cancellationToken);
    public Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken cancellationToken) => ReadAsync<PublicDevice>($"api/v1/public/devices/{id:D}", cancellationToken);
    public Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) =>
        ReadRequiredAsync<PublicPage<PublicService>>($"api/v1/public/{GroupPrefix(groupId)}services{Query(search, page)}", cancellationToken);
    public Task<PublicService?> ServiceAsync(Guid id, CancellationToken cancellationToken) => ReadAsync<PublicService>($"api/v1/public/services/{id:D}", cancellationToken);
    public Task<PublicDeviceHistory?> DeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ReadAsync<PublicDeviceHistory>($"api/v1/public/devices/{id:D}/history{Range(from, to)}", cancellationToken);
    public Task<PublicServiceHistory?> ServiceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ReadAsync<PublicServiceHistory>($"api/v1/public/services/{id:D}/history{Range(from, to)}", cancellationToken);
    public Task<PublicServiceHealthPeriods?> ServicePeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ReadAsync<PublicServiceHealthPeriods>($"api/v1/public/services/{id:D}/history/periods{Range(from, to)}", cancellationToken);

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token) where T : class
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new HttpRequestException("The public response was empty.");
    }

    private async Task<T> ReadRequiredAsync<T>(string path, CancellationToken token) where T : class =>
        await ReadAsync<T>(path, token) ?? throw new HttpRequestException("The public response is unavailable.");
    private static string GroupPrefix(Guid? id) => id is { } group ? $"groups/{group:D}/" : "";
    private static string Query(string? search, int page) => $"?page={page}&search={Uri.EscapeDataString(search ?? "")}";
    private static string Range(DateTimeOffset from, DateTimeOffset to) => $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
}

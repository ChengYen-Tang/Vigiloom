using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Network;

namespace Vigiloom.Web.Network;

internal sealed class RemotePingAdministration(HttpClient client) : IPingSettings, IPingRetentionSettings
{
    async Task<PingSettingsSnapshot> IPingSettings.GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<PingSettingsSnapshot>("internal/v1/ping/settings", token)
        ?? throw new InvalidOperationException("Network service returned empty Ping settings.");

    async Task<PingSettingsSnapshot?> IPingSettings.UpdateAsync(PingSettingsInput input, Guid actor, CancellationToken token) =>
        await SendAsync<PingSettingsSnapshot>("internal/v1/ping/settings", new PingSettingsCommand(input, actor), token);

    async Task<PingRetentionSnapshot> IPingRetentionSettings.GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<PingRetentionSnapshot>("internal/v1/ping/retention", token)
        ?? throw new InvalidOperationException("Network service returned empty Ping retention settings.");

    async Task<PingRetentionPreview?> IPingRetentionSettings.PreviewAsync(PingRetentionPreviewRequest input, Guid actor, CancellationToken token) =>
        await SendAsync<PingRetentionPreview>("internal/v1/ping/retention/preview", new PingRetentionPreviewCommand(input, actor), token);

    async Task<PingRetentionSnapshot?> IPingRetentionSettings.UpdateAsync(PingRetentionInput input, Guid actor, CancellationToken token) =>
        await SendAsync<PingRetentionSnapshot>("internal/v1/ping/retention", new PingRetentionCommand(input, actor), token);

    private async Task<T?> SendAsync<T>(string path, object input, CancellationToken token) where T : class
    {
        using var response = await client.PostAsJsonAsync(path, input, token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid Ping settings or preview.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned an empty response.");
    }
}

using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.SpeedTests;

namespace Vigiloom.Web.SpeedTests;

internal sealed class RemoteSpeedAdministration(HttpClient client) : ISpeedSettings, ISpeedRetentionSettings, ISpeedCancellation
{
    async Task<SpeedSettingsSnapshot> ISpeedSettings.GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<SpeedSettingsSnapshot>("internal/v1/speed/settings", token)
        ?? throw new InvalidOperationException("Network service returned empty speed settings.");

    async Task<SpeedSettingsSnapshot?> ISpeedSettings.UpdateAsync(SpeedSettingsInput input, Guid actor, CancellationToken token) =>
        await SendAsync<SpeedSettingsSnapshot>("internal/v1/speed/settings", new SpeedSettingsCommand(input, actor), token);

    async Task<SpeedRetentionSnapshot> ISpeedRetentionSettings.GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<SpeedRetentionSnapshot>("internal/v1/speed/retention", token)
        ?? throw new InvalidOperationException("Network service returned empty speed retention settings.");

    async Task<SpeedRetentionPreview?> ISpeedRetentionSettings.PreviewAsync(int days, string version, Guid actor, CancellationToken token) =>
        await SendAsync<SpeedRetentionPreview>("internal/v1/speed/retention/preview", new SpeedRetentionPreviewCommand(days, version, actor), token);

    async Task<SpeedRetentionSnapshot?> ISpeedRetentionSettings.UpdateAsync(SpeedRetentionInput input, Guid actor, CancellationToken token) =>
        await SendAsync<SpeedRetentionSnapshot>("internal/v1/speed/retention", new SpeedRetentionCommand(input, actor), token);

    async Task<bool> ISpeedCancellation.CancelAsync(Guid id, CancellationToken token)
    {
        using var response = await client.PostAsync($"internal/v1/speed/{id:D}/cancel", null, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<T?> SendAsync<T>(string path, object input, CancellationToken token) where T : class
    {
        using var response = await client.PostAsJsonAsync(path, input, token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid speed settings or preview.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Network service returned an empty response.");
    }
}

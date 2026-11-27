using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Web.AgentVersions;

internal sealed class InventorySettingsAccess(IServiceProvider services, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);

    public async Task<InventorySettingsView?> GetAsync(Guid? deviceId, CancellationToken token)
    {
        if (!Remote) return await services.GetRequiredService<InventoryConfiguration>().GetAsync(deviceId, token);
        using var response = await clients.CreateClient("security").GetAsync(Path(deviceId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventorySettingsView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned empty inventory settings.");
    }

    public async Task<InventorySettingsView?> UpdateAsync(Guid? deviceId, InventorySettingsUpdate update, Guid actor, CancellationToken token)
    {
        if (!Remote) return await services.GetRequiredService<InventoryConfiguration>().UpdateAsync(deviceId, update, actor, token);
        using var response = await clients.CreateClient("security").PutAsJsonAsync(Path(deviceId), new { Update = update, ActorId = actor }, token);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventorySettingsView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned empty inventory settings.");
    }

    private static string Path(Guid? deviceId) => deviceId is { } id
        ? $"internal/v1/inventory-settings?deviceId={id:D}" : "internal/v1/inventory-settings";
}

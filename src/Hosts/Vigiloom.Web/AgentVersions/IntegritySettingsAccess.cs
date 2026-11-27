using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

namespace Vigiloom.Web.AgentVersions;

internal sealed class IntegritySettingsAccess(IServiceProvider services, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    private HttpClient Client => clients.CreateClient("security");
    private static string Path(Guid deviceId) => $"internal/v1/devices/{deviceId:D}/integrity-scopes/";

    public async Task<IReadOnlyList<IntegrityScopeView>?> ListAsync(Guid deviceId, CancellationToken token)
    {
        if (!Remote) return await services.GetRequiredService<IntegrityConfiguration>().ListAsync(deviceId, token);
        using var response = await Client.GetAsync(Path(deviceId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IntegrityScopeView[]>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned empty integrity scopes.");
    }

    public Task<IntegrityConfigurationResult> CreateAsync(Guid deviceId, IntegrityScopeInput input, Guid actor, CancellationToken token) =>
        Remote ? SendAsync(HttpMethod.Post, Path(deviceId), new { Policy = input, ActorId = actor }, token)
            : services.GetRequiredService<IntegrityConfiguration>().CreateAsync(deviceId, input, actor, token);

    public Task<IntegrityConfigurationResult> UpdateAsync(Guid deviceId, Guid scopeId, IntegrityScopeInput input, string? version, Guid actor, CancellationToken token) =>
        Remote ? SendAsync(HttpMethod.Put, Path(deviceId) + scopeId.ToString("D"), new { Policy = input, Version = version, ActorId = actor }, token)
            : services.GetRequiredService<IntegrityConfiguration>().UpdateAsync(deviceId, scopeId, input, version, actor, token);

    public Task<IntegrityConfigurationResult> RetireAsync(Guid deviceId, Guid scopeId, string? version, Guid actor, CancellationToken token) =>
        Remote ? SendAsync(HttpMethod.Post, Path(deviceId) + $"{scopeId:D}/retire", new { Version = version, ActorId = actor }, token)
            : services.GetRequiredService<IntegrityConfiguration>().RetireAsync(deviceId, scopeId, version, actor, token);

    private async Task<IntegrityConfigurationResult> SendAsync<T>(HttpMethod method, string path, T command, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(command) };
        using var response = await Client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid integrity scope.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IntegrityConfigurationResult>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty integrity result.");
    }
}

using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class IntelligenceSettingsAccess(IServiceProvider services, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => configuration.GetValue("Services:Security:IntelligenceEnabled", false) &&
        !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);

    public Task<IntelligencePolicyResult> UpdateAsync(Guid sourceId, IntelligencePolicyUpdate input, Guid actor, CancellationToken token) => Remote
        ? SendAsync<IntelligencePolicyResult>(HttpMethod.Put, sourceId, "policy", input, actor, token) : services.GetRequiredService<IntelligencePolicy>().UpdateAsync(sourceId, input, actor, token);
    public Task<AdvisoryCredentialResult> UpdateAsync(Guid sourceId, AdvisoryCredentialUpdate input, Guid actor, CancellationToken token) => Remote
        ? SendAsync<AdvisoryCredentialResult>(HttpMethod.Put, sourceId, "advisory-credential", input, actor, token) : services.GetRequiredService<AdvisoryCredentials>().UpdateAsync(sourceId, input, actor, token);
    public Task<AdvisoryRecoveryResult> AbandonAsync(Guid sourceId, AdvisoryImportRecoveryRequest input, Guid actor, CancellationToken token) => Remote
        ? SendAsync<AdvisoryRecoveryResult>(HttpMethod.Post, sourceId, "advisory-recovery", input, actor, token) : services.GetRequiredService<AdvisoryRecovery>().AbandonAsync(sourceId, input, actor, token);

    private async Task<TResult> SendAsync<TResult>(HttpMethod method, Guid sourceId, string action, object input, Guid actor, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, $"internal/v1/intelligence/sources/{sourceId:D}/{action}")
        { Content = JsonContent.Create(new { Input = input, ActorId = actor }) };
        using var response = await clients.CreateClient("security").SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid intelligence settings.");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResult>(cancellationToken: token))!;
    }
}

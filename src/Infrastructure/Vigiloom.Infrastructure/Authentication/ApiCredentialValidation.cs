using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Contracts.Identity;

namespace Vigiloom.Infrastructure.Authentication;

public interface IApiCredentialValidation
{
    Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token);
}

public sealed class RemoteApiCredentialValidation(HttpClient client) : IApiCredentialValidation
{
    public async Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/api-credentials/validate",
            new VerifyApiCredentialRequest("vlg_" + value), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerifiedApiCredentialResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty credential response.");
    }
}

public static class ApiCredentialClientRegistration
{
    public static IServiceCollection AddVigiloomIdentityCredentialClient(this IServiceCollection services)
    {
        services.AddServiceDiscovery();
        services.AddHttpClient<RemoteApiCredentialValidation>((provider, client) =>
            client.BaseAddress = new Uri(provider.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddServiceDiscovery();
        services.TryAddScoped<IApiCredentialValidation>(provider => provider.GetRequiredService<RemoteApiCredentialValidation>());
        return services;
    }
}

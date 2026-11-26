using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class IntelligenceImportAccess(KevOfflineImport legacy, IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<KevOfflineImportResult> ImportAsync(Guid sourceId, Stream input, Guid actorId, CancellationToken token)
    {
        if (!configuration.GetValue("Services:Security:IntelligenceEnabled", false) || string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]))
            return await legacy.ImportAsync(sourceId, input, actorId, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"internal/v1/intelligence/sources/{sourceId:D}/offline-import?actorId={actorId:D}");
        request.Content = new StreamContent(input);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await clients.CreateClient("security").SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KevOfflineImportResult>(cancellationToken: token);
    }
}

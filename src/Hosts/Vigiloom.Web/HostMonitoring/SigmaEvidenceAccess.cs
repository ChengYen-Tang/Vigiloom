using System.Net;
using System.Net.Http.Json;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class SigmaEvidenceAccess(IServiceProvider services, IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<SigmaEvidence?> GetAsync(Guid testId, CancellationToken token)
    {
        if (!configuration.GetValue("Services:Security:IntelligenceEnabled", false) ||
            string.IsNullOrWhiteSpace(configuration["Services:Security:Url"])) return await services.GetRequiredService<SigmaEvidenceReader>().GetAsync(testId, token);
        using var response = await clients.CreateClient("security").GetAsync($"internal/v1/sigma/evidence/{testId:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SigmaEvidence>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned empty Sigma evidence.");
    }
}

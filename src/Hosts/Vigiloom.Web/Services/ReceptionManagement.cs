using System.Net.Http.Json;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Web.Services;

internal sealed class ReceptionManagement(HttpClient client, IConfiguration configuration,
    ReceptionOverview overview, ReceptionRejectionReader rejections)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);
    public async Task<IReadOnlyList<SourceReceptionSummary>> ListAsync(int page, CancellationToken token) => Local
        ? await overview.ListAsync(page, token)
        : await client.GetFromJsonAsync<SourceReceptionSummary[]>($"internal/v1/reception?page={page}", token)
            ?? throw new InvalidOperationException("Observation service returned an empty reception overview.");
    public async Task<IReadOnlyList<ReceptionRejectionCount>> RejectionsAsync(CancellationToken token) => Local
        ? await rejections.ListAsync(token)
        : await client.GetFromJsonAsync<ReceptionRejectionCount[]>("internal/v1/reception/rejections", token)
            ?? throw new InvalidOperationException("Observation service returned empty rejection summaries.");
}

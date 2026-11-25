using System.Net.Http.Json;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Resources;

internal sealed class ObservationSourceVerifier(HttpClient client) : IApplicationSourceVerifier
{
    public async Task<ApplicationSourceStatus> GetAsync(Guid sourceId, CancellationToken token) =>
        await client.GetFromJsonAsync<ApplicationSourceStatus>($"/internal/v1/observation-sources/{sourceId:D}/active", token)
        ?? throw new InvalidOperationException("Observation service returned an empty source status.");
}

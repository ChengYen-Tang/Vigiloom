using System.Net.Http.Json;
using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Resources;

internal sealed class ObservationSourceVerifier(HttpClient client) : IApplicationSourceVerifier
{
    public async Task<bool> IsActiveAsync(Guid sourceId, CancellationToken token) =>
        await client.GetFromJsonAsync<bool>($"/internal/v1/observation-sources/{sourceId:D}/active", token);
}

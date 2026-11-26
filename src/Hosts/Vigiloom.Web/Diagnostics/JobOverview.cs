using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling;

namespace Vigiloom.Web.Diagnostics;

internal sealed class JobOverview(SchedulingOverview local, IConfiguration configuration, IServiceProvider services)
{
    public async Task<WorkOverview> GetAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])) return await local.GetAsync(token);
        return await services.GetRequiredService<RemoteJobOverview>().GetAsync(token);
    }
}

internal sealed class RemoteJobOverview(HttpClient client)
{
    public async Task<WorkOverview> GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<WorkOverview>("internal/v1/diagnostics/jobs", token)
        ?? throw new InvalidOperationException("Operations service returned an empty job overview.");
}

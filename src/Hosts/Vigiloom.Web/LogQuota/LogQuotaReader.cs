using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using QuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

namespace Vigiloom.Web.LogQuota;

internal sealed class LogQuotaReader(HttpClient client, IConfiguration configuration, PolicyAdministration local)
{
    public async Task<QuotaContract> GetAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]))
            return await client.GetFromJsonAsync<QuotaContract>("internal/v1/log-quota", token)
                ?? throw new InvalidOperationException("Operations returned an empty log quota.");
        var value = await local.GetAsync(token);
        return new(value.Values.SourceRate, value.Values.SourceBurst, value.Values.GlobalRate, value.Values.GlobalBurst, value.Version);
    }
}

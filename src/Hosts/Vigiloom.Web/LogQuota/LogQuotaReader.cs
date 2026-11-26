using System.Net.Http.Json;
using System.Net;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using QuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

namespace Vigiloom.Web.LogQuota;

internal sealed class LogQuotaReader(HttpClient client, IConfiguration configuration, PolicyAdministration local, LogQuotaSettings settings)
{
    public async Task<QuotaContract> GetAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]))
            return await client.GetFromJsonAsync<QuotaContract>("internal/v1/log-quota", token)
                ?? throw new InvalidOperationException("Operations returned an empty log quota.");
        var value = await local.GetAsync(token);
        return new(value.Values.SourceRate, value.Values.SourceBurst, value.Values.GlobalRate, value.Values.GlobalBurst, value.Version);
    }

    public async Task<LogQuotaSnapshot> GetSnapshotAsync(CancellationToken token)
    {
        var value = await GetAsync(token);
        return new(new(value.SourceRate, value.SourceBurst, value.GlobalRate, value.GlobalBurst), value.Version);
    }

    public async Task<LogQuotaSnapshot?> UpdateAsync(LogQuotaValues values, string version, Guid actor, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])) return await settings.UpdateAsync(values, version, actor, token);
        var input = new QuotaContract(values.SourceRate, values.SourceBurst, values.GlobalRate, values.GlobalBurst, version);
        using var response = await client.PutAsJsonAsync("internal/v1/log-quota", new LogQuotaUpdate(input, actor), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid log quota policy.");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QuotaContract>(cancellationToken: token)
            ?? throw new InvalidOperationException("Operations returned an empty log quota result.");
        return new(new(result.SourceRate, result.SourceBurst, result.GlobalRate, result.GlobalBurst), result.Version);
    }
}

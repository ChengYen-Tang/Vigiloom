using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using QuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

namespace Vigiloom.Web.LogQuota;

internal sealed class TraceQuotaAccess(HttpClient client, IConfiguration configuration, TraceQuotaSettings local)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);
    public async Task<LogQuotaSnapshot> GetAsync(CancellationToken token)
    {
        if (Local) return await local.GetAsync(token);
        var result = await client.GetFromJsonAsync<QuotaContract>("internal/v1/trace-quota", token)
            ?? throw new InvalidOperationException("Missing trace quota policy.");
        return Map(result);
    }
    public async Task<LogQuotaSnapshot?> UpdateAsync(LogQuotaValues values, string version, Guid actor, CancellationToken token)
    {
        if (Local) return await local.UpdateAsync(values, version, actor, token);
        var input = new QuotaContract(values.SourceRate, values.SourceBurst, values.GlobalRate, values.GlobalBurst, version);
        using var response = await client.PutAsJsonAsync("internal/v1/trace-quota", new UpdateTraceQuotaCommand(input, actor), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid trace quota policy.");
        response.EnsureSuccessStatusCode();
        return Map(await response.Content.ReadFromJsonAsync<QuotaContract>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing trace quota result."));
    }
    private static LogQuotaSnapshot Map(QuotaContract value) => new(new(value.SourceRate, value.SourceBurst, value.GlobalRate, value.GlobalBurst), value.Version);
}

using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application.Retention;

namespace Vigiloom.Web.Operations;

/// <summary>Uses the Operations-owned policy after the service is configured.</summary>
internal sealed class AuditRetentionAccess(HttpClient client, IConfiguration configuration, AuditRetentionSettings local)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]);

    public async Task<AuditRetentionSnapshot> GetAsync(CancellationToken token)
    {
        if (Local) return await local.GetAsync(token);
        return await client.GetFromJsonAsync<AuditRetentionSnapshot>("internal/v1/history/retention", token)
            ?? throw new InvalidOperationException("Missing audit retention policy.");
    }

    public async Task<AuditRetentionPreview?> PreviewAsync(AuditRetentionPreviewRequest input, Guid actor, CancellationToken token)
    {
        if (Local) return await local.PreviewAsync(input, actor, token);
        using var response = await client.PostAsJsonAsync("internal/v1/history/retention/preview", new RetentionPreviewChange(actor, input), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid audit retention preview.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuditRetentionPreview>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing audit retention preview.");
    }

    public async Task<AuditRetentionSnapshot?> UpdateAsync(AuditRetentionInput input, Guid actor, CancellationToken token)
    {
        if (Local) return await local.UpdateAsync(input, actor, token);
        using var response = await client.PutAsJsonAsync("internal/v1/history/retention", new RetentionChange(actor, input), token);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid audit retention update.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuditRetentionSnapshot>(cancellationToken: token)
            ?? throw new InvalidOperationException("Missing audit retention result.");
    }
}

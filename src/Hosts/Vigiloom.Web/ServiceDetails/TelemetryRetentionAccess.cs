using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

namespace Vigiloom.Web.ServiceDetails;

/// <summary>Routes the existing retention workflow to its owning observation service after cutover.</summary>
internal sealed class TelemetryRetentionAccess(HttpClient client, IConfiguration configuration, TelemetryRetentionSettings local, IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IHostLogRetention hostLogs, ISecurityDetectionRetention securityDetections)
{
    private bool Local => string.IsNullOrWhiteSpace(configuration["Services:Observability:Url"]);

    public Task<TelemetryRetentionSnapshot> GetAsync(CancellationToken token) => Local ? local.GetAsync(token) : GetRemoteAsync(token);

    private async Task<TelemetryRetentionSnapshot> GetRemoteAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<TelemetryRetentionSnapshot>("internal/v1/telemetry-retention", token)
        ?? throw new InvalidOperationException("Observation service returned an empty retention policy.");

    public async Task<TelemetryRetentionPreview?> PreviewAsync(TelemetryRetentionPreviewRequest input, Guid actor, CancellationToken token)
    {
        if (Local) return await local.PreviewAsync(input, actor, token);
        var preview = await SendAsync<TelemetryRetentionPreview>(HttpMethod.Post, "internal/v1/telemetry-retention/preview",
            new TelemetryRetentionPreviewCommand(actor, input), token);
        if (preview is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var hostCount = await hostLogs.CountAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), preview.PreviewedAt, input.LogDays, token);
        var securityCount = await securityDetections.CountAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), preview.PreviewedAt, input.LogDays, token);
        await tx.CommitAsync(token);
        return preview with { HostLogs = hostCount, SecurityDetections = securityCount };
    }

    public Task<TelemetryRetentionSnapshot?> UpdateAsync(TelemetryRetentionInput input, Guid actor, CancellationToken token) =>
        Local ? local.UpdateAsync(input, actor, token) : SendAsync<TelemetryRetentionSnapshot>(HttpMethod.Put,
            "internal/v1/telemetry-retention", new TelemetryRetentionUpdateCommand(actor, input), token);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object input, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(input) };
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Conflict) return default;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid retention request.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Observation service returned an empty retention response.");
    }
}

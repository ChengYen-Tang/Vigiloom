using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class CustomBaselineAccess(CustomBaselineStage legacy, IBaselineSource sources, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => configuration.GetValue("Services:Security:IntelligenceEnabled", false) &&
        !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    private const string Root = "internal/v1/intelligence/custom-baselines/";

    public Task<CustomBaselineStageResult> SaveAsync(Guid? sourceId, string? expectedVersion, BaselineCheck input, Guid actorId, CancellationToken token) => Remote
        ? SendAsync<CustomBaselineStageResult>(Root + "stage", new { SourceId = sourceId, ExpectedVersion = expectedVersion, Check = input, ActorId = actorId }, token)
        : legacy.SaveAsync(sourceId, expectedVersion, input, actorId, token);
    public Task<BaselineCheck?> ReadAsync(Guid sourceId, Guid revisionId, CancellationToken token) => Remote
        ? ReadAsync<BaselineCheck>(Root + $"{sourceId:D}/revisions/{revisionId:D}", token) : legacy.ReadAsync(sourceId, revisionId, token);
    public Task<CustomBaselineDraft?> ReadDraftAsync(Guid sourceId, Guid revisionId, CancellationToken token) => Remote
        ? ReadAsync<CustomBaselineDraft>(Root + $"{sourceId:D}/revisions/{revisionId:D}/draft", token) : legacy.ReadDraftAsync(sourceId, revisionId, token);
    public async Task<IReadOnlyDictionary<Guid, BaselineCheck>> ReadVersionsAsync(Guid sourceId, IReadOnlyList<Guid> revisionIds, CancellationToken token) => Remote
        ? await SendAsync<Dictionary<Guid, BaselineCheck>>(Root + $"{sourceId:D}/versions", revisionIds, token) : await legacy.ReadVersionsAsync(sourceId, revisionIds, token);
    public async Task<int> SourcePageAsync(Guid sourceId, CancellationToken token) => Remote
        ? await ReadAsync<int>(Root + $"{sourceId:D}/page", token) : await legacy.SourcePageAsync(sourceId, token);
    public async Task<BaselineSourceSnapshot> GetActiveAsync(Guid sourceId, CancellationToken token) => Remote
        ? await ReadAsync<BaselineSourceSnapshot>(Root + $"{sourceId:D}/active", token) ?? throw new InvalidOperationException("Security returned no active baseline status.")
        : await sources.GetActiveAsync($"{BaselineSources.LocalPrefix}{sourceId:D}", token);

    private async Task<T> SendAsync<T>(string path, object body, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").PostAsJsonAsync(path, body, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty baseline response.");
    }
    private async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty baseline response.");
    }
}

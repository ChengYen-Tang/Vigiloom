using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Web.Operations;

internal sealed class LineManagement(LineChannel local, LineDeliveryStatus deliveries, LineDeliveryQueue queue,
    IConfiguration configuration, IServiceProvider services)
{
    private RemoteLine? Remote => string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]) ? null : services.GetRequiredService<RemoteLine>();
    public Task<LineChannelView> GetAsync(CancellationToken token) => Remote is { } remote ? remote.GetAsync(token) : local.GetAsync(token);
    public Task<bool> SaveAsync(LineChannelUpdate input, Guid actor, CancellationToken token) => Remote is { } remote ? remote.SaveAsync(input, actor, token) : local.SaveAsync(input, actor, token);
    public Task<bool> SetGroupAsync(Guid id, string? name, bool approved, Guid actor, CancellationToken token) =>
        Remote is { } remote ? remote.SetGroupAsync(id, name, approved, actor, token) : local.SetGroupAsync(id, name, approved, actor, token);
    public Task<Guid?> EnqueueTestAsync(Guid id, CancellationToken token) => Remote is { } remote ? remote.EnqueueTestAsync(id, token) : queue.EnqueueTestAsync(id, token);
    public Task<IReadOnlyList<LineDeliverySummary>> GetRecentAsync(CancellationToken token) => Remote is { } remote ? remote.GetRecentAsync(token) : deliveries.GetRecentAsync(token);
    public Task<LineDeliveryView?> GetDeliveryAsync(Guid id, CancellationToken token) => Remote is { } remote ? remote.GetDeliveryAsync(id, token) : deliveries.GetAsync(id, token);
    public Task<int> ReceiveAsync(HttpRequest request, CancellationToken token) => Remote is { } remote
        ? remote.ReceiveAsync(request, token)
        : new LineWebhookReceiver(local).ReceiveAsync(request.Body, request.ContentLength, request.Headers["x-line-signature"].ToString(), token);
}

internal sealed class RemoteLine(HttpClient client)
{
    public async Task<LineChannelView> GetAsync(CancellationToken token)
    {
        var result = await client.GetFromJsonAsync<LineChannelInfo>("internal/v1/line/", token)
            ?? throw new InvalidOperationException("Operations service returned an empty LINE configuration.");
        return new(result.Enabled, result.HasAccessToken, result.HasChannelSecret,
            result.Groups.Select(group => new LineGroupView(group.Id, group.ExternalId, group.Name, group.Approved, group.LastSeenAt)).ToArray(), result.AggregationSeconds);
    }
    public Task<bool> SaveAsync(LineChannelUpdate input, Guid actor, CancellationToken token) =>
        PutAsync("internal/v1/line/", new LineChannelChange(actor, input.Enabled, input.AccessToken, input.ChannelSecret, input.AggregationSeconds), token);
    public Task<bool> SetGroupAsync(Guid id, string? name, bool approved, Guid actor, CancellationToken token) =>
        PutAsync($"internal/v1/line/groups/{id:D}", new LineGroupChange(actor, name, approved), token);
    private async Task<bool> PutAsync<T>(string path, T input, CancellationToken token)
    {
        using var response = await client.PutAsJsonAsync(path, input, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
    public async Task<Guid?> EnqueueTestAsync(Guid id, CancellationToken token)
    {
        using var response = await client.PostAsync($"internal/v1/line/groups/{id:D}/test", null, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LineTestDelivery>(cancellationToken: token)
            ?? throw new InvalidOperationException("Operations service returned an empty LINE delivery.")).Id;
    }
    public async Task<IReadOnlyList<LineDeliverySummary>> GetRecentAsync(CancellationToken token)
    {
        var rows = await client.GetFromJsonAsync<LineDeliveryInfoSummary[]>("internal/v1/line/deliveries", token)
            ?? throw new InvalidOperationException("Operations service returned empty LINE deliveries.");
        return rows.Select(item => new LineDeliverySummary(item.Id, item.GroupName, item.Status, item.FailureCode, item.CreatedAt)).ToArray();
    }
    public async Task<LineDeliveryView?> GetDeliveryAsync(Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"internal/v1/line/deliveries/{id:D}", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var item = await response.Content.ReadFromJsonAsync<LineDeliveryInfo>(cancellationToken: token)
            ?? throw new InvalidOperationException("Operations service returned an empty LINE delivery.");
        return new(item.Id, item.Status, item.FailureCode, item.CreatedAt, item.CompletedAt);
    }
    public async Task<int> ReceiveAsync(HttpRequest request, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "internal/v1/line/webhook");
        message.Content = new StreamContent(request.Body);
        if (request.ContentLength is { } length) message.Content.Headers.ContentLength = length;
        message.Headers.TryAddWithoutValidation("x-line-signature", request.Headers["x-line-signature"].ToString());
        try { using var response = await client.SendAsync(message, token); return (int)response.StatusCode; }
        catch (HttpRequestException) { return StatusCodes.Status503ServiceUnavailable; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return StatusCodes.Status503ServiceUnavailable; }
    }
}

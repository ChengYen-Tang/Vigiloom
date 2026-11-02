using System.Globalization;
using Microsoft.AspNetCore.Components;
using Vigiloom.Contracts.SpeedTests;

namespace Vigiloom.Web.Client.Pages;

public partial class SpeedTests
{
    [SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [SupplyParameterFromQuery(Name = "to")] public string? To { get; set; }
    [SupplyParameterFromQuery(Name = "search")] public string? Search { get; set; }
    [SupplyParameterFromQuery(Name = "sort")] public string? Sort { get; set; }
    [SupplyParameterFromQuery(Name = "descending")] public bool? Descending { get; set; }
    [SupplyParameterFromQuery(Name = "page")] public int? Page { get; set; }
    [PersistentState] public SpeedHistoryPage? PrerenderedResult { get; set; }
    [PersistentState] public string? ResultKey { get; set; }
    private SpeedHistoryPage? Result { get; set; }
    private SpeedHistoryQuery query = new(DateTimeOffset.UtcNow.Date.AddDays(-7), DateTimeOffset.UtcNow.Date.AddDays(1));
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? activeLoad;
    private Task? refresh;
    private bool loading, error, unauthorized, invalid;
    protected override async Task OnParametersSetAsync()
    {
        Result ??= PrerenderedResult;
        var from = ParseDate(From, DateTime.UtcNow.Date.AddDays(-7));
        var to = ParseDate(To, DateTime.UtcNow.Date);
        invalid = from is null || to is null || to < from || to - from >= TimeSpan.FromDays(31) ||
            Page is < 1 or > 10000 || Search?.Length is > 100 || Sort is not (null or "time" or "download" or "upload");
        if (invalid) { Result = null; return; }
        query = new(new DateTimeOffset(from!.Value, TimeSpan.Zero), new DateTimeOffset(to!.Value.AddDays(1), TimeSpan.Zero), Search, Sort ?? "time", Descending ?? true, Page ?? 1);
        if (Result is not null && ResultKey == QueryKey(query))
        {
            try { if (await Identity.GetAsync(lifetime.Token) is null) { Result = null; unauthorized = true; } }
            catch (Exception ex) when (ex is not OperationCanceledException) { Result = null; error = true; }
            return;
        }
        await LoadAsync();
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && RendererInfo.IsInteractive) refresh = RefreshAsync();
        return Task.CompletedTask;
    }
    private async Task RefreshAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await InvokeAsync(async () => { if (!loading && !invalid) await LoadAsync(); StateHasChanged(); }); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task LoadAsync()
    {
        activeLoad?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeLoad = load; loading = true; error = unauthorized = false;
        try
        {
            var result = await History.QueryAsync(query, load.Token);
            load.Token.ThrowIfCancellationRequested();
            Result = result; ResultKey = QueryKey(query);
            // Large persisted payloads can exceed SignalR's 32 KiB circuit-start limit.
            // For larger histories, reload after activation instead of persisting them.
            PrerenderedResult = !RendererInfo.IsInteractive && result.Items.Count + result.Trend.Count <= 20 &&
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result).Length <= 8192 ? result : null;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { Result = null; ResultKey = null; unauthorized = true; }
        catch (Exception ex) { Result = null; ResultKey = null; error = true; Logger.LogWarning("Could not query speed history: {ExceptionType}", ex.GetType().Name); }
        finally { if (ReferenceEquals(activeLoad, load)) { loading = false; activeLoad = null; } }
    }
    private async Task ChangeQuery(SpeedHistoryQuery value)
    {
        query = value;
        await LoadAsync();
    }
    private Task ChangePage(int page) => ChangeQuery(query with { Page = page });
    private string FilterUrl => Navigation.ToAbsoluteUri(Navigation.GetUriWithQueryParameters("/speed-tests", new Dictionary<string, object?>
    {
        ["from"] = query.From.ToString("yyyy-MM-dd"), ["to"] = query.To.AddDays(-1).ToString("yyyy-MM-dd"), ["search"] = query.Search,
        ["sort"] = query.Sort, ["descending"] = query.Descending, ["page"] = query.Page
    })).PathAndQuery;
    private static string QueryKey(SpeedHistoryQuery value) => System.Text.Json.JsonSerializer.Serialize(value);
    private static DateTime? ParseDate(string? value, DateTime fallback) => value is null ? DateTime.SpecifyKind(fallback, DateTimeKind.Unspecified)
        : DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < DateTime.MaxValue.Date ? date : null;
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (refresh is not null) await refresh;
        lifetime.Dispose();
    }
}

using System.Globalization;
using Microsoft.AspNetCore.Components;
using Vigiloom.Contracts.Network;
using Vigiloom.Web.Client.Components;

namespace Vigiloom.Web.Client.Components.PingHistory;

public partial class PingHistoryPanel
{
    [Parameter] public Guid DeviceId { get; set; }
    [Parameter] public string InitialTarget { get; set; } = "";
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? active;
    private Guid loadedDevice;
    private PingHistoryQuery? query;
    private PingHistoryPage? result;
    private IReadOnlyList<PingHistoryOption> options = [];
    private IReadOnlyList<TimeSeriesPoint> points = [];
    private string startDate = "", endDate = "";
    private string optionSearch = "", sortOrder = "desc";
    private int selection;
    private bool loading, invalid;
    private string? error;

    protected override async Task OnParametersSetAsync()
    {
        if (!RendererInfo.IsInteractive) { loading = true; return; }
        if (loadedDevice == DeviceId) return;
        loadedDevice = DeviceId;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        startDate = endDate = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        options = [new(InitialTarget, "central")]; selection = 0;
        await ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        var validStart = DateOnly.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
        var validEnd = DateOnly.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
        invalid = !validStart || !validEnd
            || end < start || end.DayNumber - start.DayNumber >= 7 || end == DateOnly.MaxValue;
        if (invalid || selection < 0 || selection >= options.Count) return;
        var chosen = options[selection];
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var to = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        query = new(DeviceId, from, to, chosen.Target, chosen.Location, Descending: sortOrder == "desc");
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (query is null) return;
        active?.Cancel();
        var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        active = current; loading = true; error = null;
        try
        {
            var fetched = await Client.QueryAsync(query, current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (fetched is null) { result = null; error = "找不到設備或無法讀取 Ping 歷史。"; return; }
            result = fetched;
            var chosen = new PingHistoryOption(query.Target, query.Location);
            var list = fetched.Options.ToList();
            if (!list.Contains(chosen)) list.Insert(0, chosen);
            options = list; selection = list.IndexOf(chosen);
            var buckets = fetched.Buckets.ToDictionary(x => x.Start);
            var series = new List<TimeSeriesPoint>((int)((query.To - query.From).TotalMinutes / 5));
            for (var time = query.From; time < query.To; time = time.AddMinutes(5))
                series.Add(new(time, buckets.TryGetValue(time, out var bucket) ? bucket.AverageMilliseconds : null));
            points = series;
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { result = null; error = "登入已過期或權限已變更，請重新登入。"; }
        catch (Exception ex) { Logger.LogWarning(ex, "Unable to read Ping history"); result = null; error = "無法載入 Ping 歷史，請稍後再試。"; }
        finally { if (ReferenceEquals(active, current)) { active = null; loading = false; } current.Dispose(); }
    }

    private async Task PreviousAsync() { if (query is not null && query.Page > 1) { query = query with { Page = query.Page - 1 }; await ReloadAsync(); } }
    private async Task NextAsync() { if (query is not null && query.Page < 10000) { query = query with { Page = query.Page + 1 }; await ReloadAsync(); } }
    private string AverageLatency()
    {
        if (result is null || result.Buckets.Count == 0) return "無資料";
        if (result.RollupsPending) return "補算中";
        if (result.Buckets.Any(x => x.LatencyOverflow)) return "無法計算";
        double count = 0, mean = 0;
        foreach (var bucket in result.Buckets)
        {
            if (bucket.AverageMilliseconds is not { } average || bucket.Successes == 0) continue;
            var total = count + bucket.Successes;
            mean += (average - mean) * (bucket.Successes / total);
            count = total;
        }
        return count == 0 ? "無有效延遲" : $"{mean:0.##} ms";
    }
    private string ExtremeLatency(bool minimum)
    {
        if (result is null || result.Buckets.Count == 0) return "無資料";
        if (result.RollupsPending) return "補算中";
        var values = result.Buckets.Select(x => minimum ? x.MinimumMilliseconds : x.MaximumMilliseconds).OfType<double>().ToArray();
        if (values.Length == 0) return "無有效延遲";
        return $"{(minimum ? values.Min() : values.Max()):0.##} ms";
    }
    private static string OptionName(PingHistoryOption item) => $"{item.Target ?? "舊紀錄（目標未記錄）"} · {item.Location}";
    private bool MatchesOption(PingHistoryOption item) => string.IsNullOrWhiteSpace(optionSearch) || OptionName(item).Contains(optionSearch.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string OutcomeName(PingHistorySample item) => item.Outcome switch
    {
        PingOutcome.Success => "成功", PingOutcome.Timeout => "逾時", PingOutcome.Unreachable => "無法抵達",
        PingOutcome.DnsError => "名稱解析失敗", PingOutcome.PermissionDenied => "量測權限不足",
        PingOutcome.MonitorError => "監測程序錯誤", _ => item.Succeeded ? "成功" : "失敗（原因未記錄）"
    };
    public ValueTask DisposeAsync() { lifetime.Cancel(); active?.Cancel(); lifetime.Dispose(); return ValueTask.CompletedTask; }
}

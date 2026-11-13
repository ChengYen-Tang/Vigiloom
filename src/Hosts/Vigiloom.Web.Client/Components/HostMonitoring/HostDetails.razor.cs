using Microsoft.AspNetCore.Components;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Web.Client.Components;

namespace Vigiloom.Web.Client.Components.HostMonitoring;

public partial class HostDetails
{
    [Parameter] public Guid DeviceId { get; set; }
    private static readonly (string Key, string Label)[] Tabs = [("inventory", "資產盤點"), ("metrics", "效能"), ("process", "程序與服務"), ("logs", "主機日誌"), ("integrity", "檔案完整性")];
    private static readonly (AgentMetricKind Kind, string Label)[] MetricKinds = [(AgentMetricKind.CpuTotalPercent, "CPU 使用率"), (AgentMetricKind.CpuCorePercent, "CPU 核心使用率"), (AgentMetricKind.MemoryTotalBytes, "總記憶體"), (AgentMetricKind.MemoryAvailableBytes, "可用記憶體"), (AgentMetricKind.VolumeTotalBytes, "總磁碟空間"), (AgentMetricKind.VolumeAvailableBytes, "可用磁碟空間"), (AgentMetricKind.DiskReadBytesPerSecond, "磁碟讀取"), (AgentMetricKind.DiskWriteBytesPerSecond, "磁碟寫入"), (AgentMetricKind.NetworkReceivedBytesPerSecond, "網路接收"), (AgentMetricKind.NetworkSentBytesPerSecond, "網路傳送"), (AgentMetricKind.ProcessCpuPercent, "程序 CPU 使用率"), (AgentMetricKind.ProcessWorkingSetBytes, "程序工作集記憶體")];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? activeLoad;
    private Guid loadedDevice;
    private string tab = "inventory";
    private int rangeHours = 24;
    private bool loading, unauthorized;
    private string? error;
    private IReadOnlyList<InventorySourceView> sources = [];
    private InventorySourceView? selected;
    private InventorySource selectedSource;
    private Guid? snapshotId;
    private bool usePartial = true;
    private DateTimeOffset? snapshotAt;
    private int chunkIndex;
    private InventorySnapshotChunkView? chunk;
    private HostMetricPage? metricPage;
    private readonly Stack<string?> metricCursors = new();
    private string? metricCursor;
    private AgentMetricKind metricKind = AgentMetricKind.CpuTotalPercent;
    private List<HostMetricValue> series = [];
    private int seriesIndex;
    private IReadOnlyList<TimeSeriesPoint> points = [];
    private HostLogPage? logs;
    private readonly Stack<string?> logCursors = new();
    private string? logCursor;
    private DeviceIntegrityView? integrity;
    private IntegrityChangePage? changes;
    private readonly Stack<string?> changeCursors = new();
    private string? changeCursor;
    private Guid? baselineScopeId, baselineSnapshotId;
    private IntegritySnapshotChunkView? baseline;
    private DateTimeOffset from, until;

    protected override async Task OnParametersSetAsync()
    {
        // The same Auto component is instantiated again during activation; wait until
        // then so a large snapshot is neither queried twice nor serialized into the page.
        if (!RendererInfo.IsInteractive) { loading = true; return; }
        if (loadedDevice == DeviceId) return;
        loadedDevice = DeviceId;
        tab = "inventory";
        await ReloadAsync();
    }

    private async Task ChangeTabAsync(string value) { tab = value; await ReloadAsync(); }
    private async Task ReloadAsync()
    {
        until = DateTimeOffset.UtcNow;
        from = until.AddHours(-rangeHours);
        await RunAsync(async token =>
        {
            switch (tab)
            {
                case "inventory": case "process":
                    var inventory = await Monitoring.GetInventoryAsync(DeviceId, token);
                    token.ThrowIfCancellationRequested();
                    if (inventory is null) throw new InvalidOperationException("Device missing");
                    sources = inventory.Sources.Where(x => tab == "process" ? x.Source >= InventorySource.HostProcesses : x.Source < InventorySource.HostProcesses).ToArray();
                    selected = sources.FirstOrDefault();
                    if (selected is not null) { selectedSource = selected.Source; usePartial = true; await LoadSelectedAsync(token); }
                    break;
                case "metrics": await LoadMetricsAsync(token); break;
                case "logs":
                    logCursor = null; logCursors.Clear();
                    var firstLogs = await Monitoring.GetLogsAsync(DeviceId, from, until, null, token);
                    token.ThrowIfCancellationRequested(); logs = firstLogs ?? throw new InvalidOperationException("Device missing"); break;
                case "integrity":
                    baseline = null; baselineScopeId = baselineSnapshotId = null;
                    changeCursor = null; changeCursors.Clear();
                    var state = await Monitoring.GetIntegrityAsync(DeviceId, token);
                    var firstChanges = await Monitoring.GetIntegrityChangesAsync(DeviceId, from, until, null, token);
                    token.ThrowIfCancellationRequested(); integrity = state ?? throw new InvalidOperationException("Device missing"); changes = firstChanges ?? throw new InvalidOperationException("Device missing"); break;
            }
        });
    }
    private async Task ChangeRangeAsync(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var value) && value is 24 or 168 or 720) { rangeHours = value; await ReloadAsync(); }
    }
    private async Task ChangeSourceAsync(ChangeEventArgs args)
    {
        if (!int.TryParse(args.Value?.ToString(), out var value)) return;
        selectedSource = (InventorySource)value;
        selected = sources.FirstOrDefault(x => x.Source == selectedSource);
        usePartial = true;
        await RunAsync(LoadSelectedAsync);
    }
    private async Task ChangeSnapshotAsync(ChangeEventArgs args)
    {
        usePartial = args.Value?.ToString() == "partial";
        await RunAsync(LoadSelectedAsync);
    }
    private async Task LoadSelectedAsync(CancellationToken token)
    {
        chunk = null; chunkIndex = 0;
        var partial = usePartial && selected?.PartialSnapshotId is not null;
        snapshotId = partial ? selected!.PartialSnapshotId : selected?.CurrentSnapshotId;
        snapshotAt = partial ? selected!.PartialObservedAt : selected?.CurrentObservedAt;
        if (snapshotId is { } id) await FetchChunkAsync(id, 0, token);
    }
    private Task LoadChunkAsync(int index) => snapshotId is { } id ? RunAsync(token => FetchChunkAsync(id, index, token)) : Task.CompletedTask;
    private async Task FetchChunkAsync(Guid id, int index, CancellationToken token)
    {
        var result = await Monitoring.GetInventoryChunkAsync(DeviceId, selectedSource, id, index, token);
        token.ThrowIfCancellationRequested();
        if (result is null) { chunk = null; snapshotId = null; error = "快照已更新，請重新載入最新資料。"; return; }
        chunk = result; chunkIndex = index;
    }
    private async Task ChangeMetricAsync(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var value)) { metricKind = (AgentMetricKind)value; await RunAsync(LoadMetricsAsync); }
    }
    private Task LoadMetricsAsync(CancellationToken token) { metricCursors.Clear(); metricCursor = null; return LoadMetricPageAsync(null, token); }
    private async Task LoadMetricPageAsync(string? cursor, CancellationToken token)
    {
        var page = await Monitoring.GetMetricsAsync(DeviceId, from, until, cursor, metricKind, token);
        token.ThrowIfCancellationRequested();
        metricPage = page ?? throw new InvalidOperationException("Device missing");
        series = page.Items.DistinctBy(x => (x.AgentId, x.Source, x.Dimension)).ToList();
        seriesIndex = 0; points = [];
        if (series.Count > 0) await LoadSeriesAsync(token);
    }
    private Task NextMetricsAsync() => metricPage?.NextCursor is { } cursor ? NavigateMetricsAsync(cursor, true) : Task.CompletedTask;
    private Task PreviousMetricsAsync() => metricCursors.TryPeek(out var cursor) ? NavigateMetricsAsync(cursor, false) : Task.CompletedTask;
    private Task NavigateMetricsAsync(string? cursor, bool forward) => RunAsync(async token =>
    {
        await LoadMetricPageAsync(cursor, token);
        token.ThrowIfCancellationRequested();
        if (forward) metricCursors.Push(metricCursor); else metricCursors.Pop();
        metricCursor = cursor;
    });
    private async Task ChangeSeriesAsync(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var index) && index >= 0 && index < series.Count) { seriesIndex = index; await RunAsync(LoadSeriesAsync); }
    }
    private async Task LoadSeriesAsync(CancellationToken token)
    {
        var item = series[seriesIndex];
        var bucketSeconds = rangeHours <= 24 ? 300 : rangeHours <= 168 ? 3600 : 14400;
        var result = await Monitoring.AggregateMetricsAsync(DeviceId, item.AgentId, from, until, metricKind, item.Source, item.Dimension, bucketSeconds, token);
        token.ThrowIfCancellationRequested();
        if (result is null) throw new InvalidOperationException("Agent missing");
        var output = new List<TimeSeriesPoint>();
        DateTimeOffset? last = null;
        foreach (var bucket in result.Buckets)
        {
            if (last is { } time && bucket.Start - time > TimeSpan.FromSeconds(result.BucketSeconds)) output.Add(new(time.AddSeconds(result.BucketSeconds), null));
            output.Add(new(bucket.Start, bucket.Average)); last = bucket.Start;
        }
        points = output;
    }
    private Task NextLogsAsync() => logs?.NextCursor is { } cursor ? NavigateLogsAsync(cursor, true) : Task.CompletedTask;
    private Task PreviousLogsAsync() => logCursors.TryPeek(out var cursor) ? NavigateLogsAsync(cursor, false) : Task.CompletedTask;
    private Task NavigateLogsAsync(string? cursor, bool forward) => RunAsync(async token =>
    {
        var page = await Monitoring.GetLogsAsync(DeviceId, from, until, cursor, token);
        token.ThrowIfCancellationRequested(); logs = page ?? throw new InvalidOperationException("Device missing");
        if (forward) logCursors.Push(logCursor); else logCursors.Pop();
        logCursor = cursor;
    });
    private Task NextChangesAsync() => changes?.NextCursor is { } cursor ? NavigateChangesAsync(cursor, true) : Task.CompletedTask;
    private Task PreviousChangesAsync() => changeCursors.TryPeek(out var cursor) ? NavigateChangesAsync(cursor, false) : Task.CompletedTask;
    private Task NavigateChangesAsync(string? cursor, bool forward) => RunAsync(async token =>
    {
        var page = await Monitoring.GetIntegrityChangesAsync(DeviceId, from, until, cursor, token);
        token.ThrowIfCancellationRequested(); changes = page ?? throw new InvalidOperationException("Device missing");
        if (forward) changeCursors.Push(changeCursor); else changeCursors.Pop();
        changeCursor = cursor;
    });
    private Task OpenBaselineAsync(IntegrityScopeMonitoringView scope)
    {
        baseline = null; baselineScopeId = scope.ScopeId; baselineSnapshotId = scope.SnapshotId;
        return LoadBaselineAsync(0);
    }
    private Task LoadBaselineAsync(int index) => baselineScopeId is { } scope && baselineSnapshotId is { } snapshot
        ? RunAsync(async token =>
        {
            var result = await Monitoring.GetIntegrityChunkAsync(DeviceId, scope, snapshot, index, token);
            token.ThrowIfCancellationRequested();
            if (result is null) { baseline = null; baselineSnapshotId = null; error = "基線已更新，請重新載入最新資料。"; return; }
            baseline = result;
        }) : Task.CompletedTask;
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        activeLoad?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeLoad = load; loading = true; error = null; unauthorized = false;
        try { await action(load.Token); }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) when (!load.IsCancellationRequested) { unauthorized = true; }
        catch (Exception ex) when (!load.IsCancellationRequested) { Logger.LogWarning("Could not load host monitoring data: {ExceptionType}", ex.GetType().Name); error = "無法讀取此項資料，請稍後重試。"; }
        catch (Exception) when (load.IsCancellationRequested) { }
        finally { if (ReferenceEquals(activeLoad, load)) { activeLoad = null; loading = false; } }
    }
    public async ValueTask DisposeAsync() { await lifetime.CancelAsync(); lifetime.Dispose(); }
    private static string DisplayTime(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚無資料";
    private static string AgeNotice(DateTimeOffset? value) => value is null ? "" : value > DateTimeOffset.UtcNow ? "（時間異常）" : DateTimeOffset.UtcNow - value > TimeSpan.FromHours(24) ? "（超過 24 小時）" : "";
    private static string OutcomeName(InventoryOutcome? value) => value switch { InventoryOutcome.Complete => "完整", InventoryOutcome.Partial => "部分", InventoryOutcome.PermissionDenied => "權限不足", InventoryOutcome.Unsupported => "不支援", InventoryOutcome.Error => "採集失敗", _ => "尚無回報" };
    private static string IntegrityOutcomeName(IntegrityScanOutcome? value) => value switch { IntegrityScanOutcome.Complete => "完整", IntegrityScanOutcome.Partial => "部分", IntegrityScanOutcome.PermissionDenied => "權限不足", IntegrityScanOutcome.Unsupported => "不支援", IntegrityScanOutcome.Error => "掃描失敗", _ => "尚無回報" };
    private static string InventoryStatusName(InventoryCompletion? value) => value switch { InventoryCompletion.Complete => "已接受完整資料", InventoryCompletion.Partial => "部分資料", InventoryCompletion.Expired => "接收逾期", InventoryCompletion.Rejected => "已拒收", _ => "尚未完成" };
    private static string IntegrityStatusName(IntegrityCompletion? value) => value switch { IntegrityCompletion.Complete => "已接受完整資料", IntegrityCompletion.Partial => "部分資料", IntegrityCompletion.Expired => "接收逾期", IntegrityCompletion.Rejected => "已拒收", _ => "尚未完成" };
    internal static string SourceName(InventorySource source) => source switch { InventorySource.WindowsSoftware32 => "Windows 軟體（32 位元）", InventorySource.WindowsSoftware64 => "Windows 軟體（64 位元）", InventorySource.LinuxDpkg => "Debian 軟體", InventorySource.LinuxRpm => "RPM 軟體", InventorySource.LinuxAccounts => "Linux 帳號", InventorySource.WindowsAccounts => "Windows 帳號", InventorySource.LinuxGroups => "Linux 群組", InventorySource.WindowsGroups => "Windows 群組", InventorySource.WindowsQuickFix => "Windows 修補程式", InventorySource.HostSystem => "系統資訊", InventorySource.HostNetwork => "網路介面", InventorySource.HostVolumes => "磁碟空間", InventorySource.HostProcesses => "程序", InventorySource.HostServices => "作業系統服務", InventorySource.HostListeners => "監聽連線", _ => source.ToString() };
    internal static string EntryDetails(InventoryEntry entry)
    {
        var fields = new List<string> { $"識別：{entry.Key}" };
        void Add(string name, object? value) { if (value is not null && !string.IsNullOrWhiteSpace(value.ToString())) fields.Add($"{name}：{value}"); }
        Add("版本", entry.Version); Add("作業系統", entry.OperatingSystem); Add("架構", entry.Architecture);
        Add("作業系統系列", entry.OperatingSystemFamily); Add("發行版", entry.DistributionId); Add("發行版版本", entry.DistributionVersion);
        Add("程序架構", entry.ProcessArchitecture); Add("執行階段", entry.Runtime); Add("Agent 版本", entry.AgentVersion); Add("可用處理器數", entry.AvailableProcessorCount);
        Add("供應商", entry.Supplier); Add("安裝日期", entry.InstalledOn); Add("帳號／群組 ID", entry.NumericId); Add("主要群組 ID", entry.PrimaryGroupId);
        Add("啟動時間（UTC）", entry.BootedAt?.ToUniversalTime().ToString("u")); Add("記憶體（bytes）", entry.TotalMemoryBytes);
        Add("容量（bytes）", entry.CapacityBytes); Add("位址", entry.Address); Add("連接埠", entry.Port); Add("通訊協定", entry.Protocol);
        Add("狀態", entry.ServiceStatus); Add("啟動模式", entry.StartupMode); Add("執行身分", entry.RunAs);
        Add("執行檔", entry.ExecutablePath); Add("命令列", entry.CommandLine); Add("父程序 ID", entry.ParentProcessId);
        Add("程序啟動時間（UTC）", entry.ProcessStartedAt?.ToUniversalTime().ToString("u")); Add("擁有程序 ID", entry.OwningProcessId);
        return string.Join(" · ", fields);
    }
    private static string SeriesName(HostMetricValue item) => $"{item.Source}{(item.Dimension is null ? "" : " · " + item.Dimension)} · Agent {item.AgentId:D}";
    private static string MetricUnit(AgentMetricKind kind) => kind is AgentMetricKind.CpuTotalPercent or AgentMetricKind.CpuCorePercent or AgentMetricKind.ProcessCpuPercent ? "%" : kind is AgentMetricKind.MemoryTotalBytes or AgentMetricKind.MemoryAvailableBytes or AgentMetricKind.VolumeTotalBytes or AgentMetricKind.VolumeAvailableBytes or AgentMetricKind.ProcessWorkingSetBytes ? "bytes" : "bytes/s";
}

using Microsoft.AspNetCore.Components;
using System.Globalization;
using Vigiloom.Contracts.Services;

namespace Vigiloom.Web.Client.Components.Services;

public partial class ServiceOverviewPanel
{
    [Parameter] public Guid ServiceId { get; set; }
    private readonly CancellationTokenSource lifetime = new();
    private ServiceDetails? service;
    private ServiceInstancePage? instances;
    private DeploymentLinkPage? deployments;
    private bool deploymentsLoading, deploymentsError;
    private int deploymentsPage = 1;
    private ServiceHealthStatus? health;
    private ServiceHealthHistory? history;
    private ServiceHttpMetrics? httpMetrics;
    private HealthObservationPage? healthEvidence;
    private ServiceEvidencePage<ServiceLogEvidence>? logs;
    private ServiceEvidencePage<ServiceTraceEvidence>? traces;
    private bool loading = true, historyError, healthError, instancesError, metricsError, interactive, healthEvidenceLoading, healthEvidenceError;
    private string? error, rangeError, evidenceError, activeTraceId;
    private int page = 1, evidencePage = 1, historyPage = 1, selectedRange = 24, healthEvidencePage = 1;
    private long evidenceGeneration;
    private CancellationTokenSource? evidenceRequest;
    private bool showTraces, evidenceLoading;
    private bool EvidenceHasMore => showTraces ? traces?.HasMore == true : logs?.HasMore == true;
    private string startText = "", endText = "";

    protected override async Task OnParametersSetAsync()
    {
        ClearEvidence();
        healthEvidence = null; healthEvidencePage = 1;
        deployments = null; deploymentsPage = 1; deploymentsError = false;
        await LoadAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) { interactive = true; StateHasChanged(); }
    }

    private async Task LoadAsync()
    {
        loading = true; error = null;
        try
        {
            service = await Client.GetServiceAsync(ServiceId, lifetime.Token);
            if (service is null) return;
            if (string.IsNullOrEmpty(startText)) SetRange(selectedRange, service.ServerTime);
            await LoadHealthAsync();
            await LoadInstancesAsync();
            await LoadHistoryAsync();
            await LoadHttpMetricsAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { error = "登入已失效或沒有權限，請重新登入。"; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load service details"); error = "無法載入服務資料，請稍後再試。"; }
        finally { loading = false; }
    }

    private async Task LoadHttpMetricsAsync()
    {
        metricsError = false;
        try { httpMetrics = await Client.GetHttpMetricsAsync(ServiceId, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load service HTTP metrics"); httpMetrics = null; metricsError = true; }
    }

    private async Task LoadHealthAsync()
    {
        healthError = false;
        try { health = await Client.GetHealthAsync(ServiceId, lifetime.Token); if (health is null) healthError = true; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load service health"); health = null; healthError = true; }
    }

    private async Task LoadHealthEvidenceAsync(int targetPage)
    {
        if (healthEvidenceLoading) return;
        healthEvidenceLoading = true; healthEvidenceError = false;
        var id = ServiceId;
        try { var result = await Client.GetHealthEvidenceAsync(id, targetPage, lifetime.Token); if (id == ServiceId) { healthEvidence = result; healthEvidencePage = targetPage; } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { healthEvidence = null; error = "登入已失效或沒有權限，請重新登入。"; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load health evidence"); healthEvidence = null; healthEvidenceError = true; }
        finally { healthEvidenceLoading = false; }
    }

    private async Task LoadInstancesAsync()
    {
        instancesError = false;
        try { instances = await Client.GetInstancesAsync(ServiceId, page, lifetime.Token); if (instances is null) instancesError = true; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load service instances"); instances = null; instancesError = true; }
    }

    private async Task LoadDeploymentsAsync(int targetPage)
    {
        if (deploymentsLoading) return;
        deploymentsLoading = true; deploymentsError = false;
        var id = ServiceId;
        try { var result = await Client.GetDeploymentsAsync(id, targetPage, lifetime.Token); if (id == ServiceId) { deployments = result; deploymentsPage = targetPage; } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { deployments = null; error = "登入已失效或沒有權限，請重新登入。"; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load deployment relationships"); deployments = null; deploymentsError = true; }
        finally { deploymentsLoading = false; }
    }

    private async Task LoadHistoryAsync()
    {
        historyError = false;
        try
        {
            if (!TryRange(out var start, out var end)) return;
            history = await Client.GetHistoryAsync(ServiceId, start, end, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { throw; }
        catch (ArgumentException) { history = null; historyError = true; rangeError = "請確認時間範圍沒有超過目前時間，且最多 366 天。"; }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        { history = null; historyError = true; rangeError = "請確認時間範圍沒有超過目前時間，且最多 366 天。"; }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to load service health history"); history = null; historyError = true; }
    }

    private void SetRange(int hours, DateTimeOffset serverTime)
    {
        var end = serverTime.AddSeconds(-1);
        startText = end.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        endText = end.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }
    private bool TryRange(out DateTimeOffset start, out DateTimeOffset end)
    {
        string[] formats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"];
        var validStart = DateTime.TryParseExact(startText, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from);
        var validEnd = DateTime.TryParseExact(endText, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var to);
        start = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc));
        end = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc));
        if (!validStart || !validEnd || start >= end || end - start > TimeSpan.FromDays(366))
        { rangeError = "請選擇有效的 UTC 時間範圍：開始須早於結束，最多 366 天。"; return false; }
        rangeError = null;
        return true;
    }
    private async Task ApplyRangeAsync() { if (TryRange(out _, out _)) { historyPage = 1; ClearEvidence(); await LoadAsync(); } }
    private void ChangeStart(ChangeEventArgs change) { startText = change.Value?.ToString() ?? ""; selectedRange = 0; ClearEvidence(); }
    private void ChangeEnd(ChangeEventArgs change) { endText = change.Value?.ToString() ?? ""; selectedRange = 0; ClearEvidence(); }
    private async Task ChangeRangeAsync(int value) { selectedRange = value; startText = ""; historyPage = 1; ClearEvidence(); await LoadAsync(); }
    private void ClearEvidence()
    {
        evidenceGeneration++;
        evidenceRequest?.Cancel(); evidenceRequest?.Dispose(); evidenceRequest = null;
        logs = null; traces = null; evidenceError = null; evidencePage = 1; evidenceLoading = false; activeTraceId = null;
    }
    private async Task LoadEvidenceAsync(bool trace, int targetPage, string? traceId = null)
    {
        if (!TryRange(out var from, out var to)) return;
        if (to - from > TimeSpan.FromDays(31)) { evidenceError = "查詢 Logs 或 Traces 時，請將時間範圍縮短至 31 天以內。"; return; }
        ClearEvidence();
        showTraces = trace;
        activeTraceId = trace ? null : traceId;
        var generation = evidenceGeneration;
        evidenceRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var requestToken = evidenceRequest.Token;
        evidenceLoading = true;
        try
        {
            if (trace)
            {
                var result = await Client.GetTracesAsync(ServiceId, from, to, targetPage, requestToken);
                if (generation != evidenceGeneration) return;
                traces = result;
            }
            else
            {
                var result = await Client.GetLogsAsync(ServiceId, from, to, targetPage, activeTraceId, requestToken);
                if (generation != evidenceGeneration) return;
                logs = result;
            }
            evidencePage = targetPage;
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { if (generation == evidenceGeneration) evidenceError = "登入已失效或沒有權限，請重新登入。"; }
        catch (Exception ex) { if (generation == evidenceGeneration) { Logger.LogWarning(ex, "Failed to load service evidence"); evidenceError = "無法載入證據，請稍後再試。"; } }
        finally { if (generation == evidenceGeneration) evidenceLoading = false; }
    }
    private async Task PreviousAsync() { if (page > 1) { page--; await LoadAsync(); } }
    private async Task NextAsync() { if (instances?.HasMore == true) { page++; await LoadAsync(); } }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private static string LastReceived(DateTimeOffset? value) => value is { } received ? Format(received) : "尚無資料";
    private static string MetricStatus(string? state) => state switch
    {
        "Busy" => "目前有其他指標查詢正在執行，請稍後重新載入。",
        "Changed" => "指標資料正在更新，請稍後重新載入。",
        "AmbiguousSource" => "指標來源有重疊或無法區分，暫不計算以免重複統計。",
        "Incomplete" => "指標時間範圍或資料品質不足，暫無法可靠計算。",
        _ => "尚未收到可用的 HTTP 指標資料。"
    };
    private static string HealthReasons(ServiceHealthUnknownReason reasons)
    {
        var labels = new List<string>();
        if (reasons.HasFlag(ServiceHealthUnknownReason.NotConfigured)) labels.Add("尚未設定健康檢查");
        if (reasons.HasFlag(ServiceHealthUnknownReason.Disabled)) labels.Add("健康檢查已停用");
        if (reasons.HasFlag(ServiceHealthUnknownReason.Waiting)) labels.Add("等待足夠的健康資料");
        if (reasons.HasFlag(ServiceHealthUnknownReason.Expired)) labels.Add("部分健康資料已過期");
        if (reasons.HasFlag(ServiceHealthUnknownReason.Invalidated)) labels.Add("健康檢查或授權狀態已變更，等待有效資料");
        return string.Join("；", labels);
    }
    private static string TimelineWidth(ServiceHealthInterval interval) => $"flex-grow:{Math.Max(0.001, (interval.End - interval.Start).TotalSeconds).ToString(CultureInfo.InvariantCulture)}";
    private static string HealthClass(ServiceHealthState? state) => state switch
    { ServiceHealthState.Healthy => "health-healthy", ServiceHealthState.Degraded => "health-degraded", ServiceHealthState.Unhealthy => "health-unhealthy", _ => "health-unknown" };
    private static string HealthName(ServiceHealthState? state) => state switch
    { ServiceHealthState.Healthy => "正常", ServiceHealthState.Degraded => "降級", ServiceHealthState.Unhealthy => "異常", _ => "未知或資料不足" };
    private static string ObservationName(HealthCheckObservationKind kind) => kind switch
    {
        HealthCheckObservationKind.Healthy => "正常", HealthCheckObservationKind.Degraded => "降級", HealthCheckObservationKind.Unhealthy => "異常",
        HealthCheckObservationKind.Timeout => "檢查逾時", HealthCheckObservationKind.TransportError => "連線失敗",
        HealthCheckObservationKind.InvalidResponse => "回應無效", HealthCheckObservationKind.NetworkDenied => "網路政策拒絕", _ => "未知"
    };
    public ValueTask DisposeAsync() { lifetime.Cancel(); ClearEvidence(); lifetime.Dispose(); return ValueTask.CompletedTask; }
}

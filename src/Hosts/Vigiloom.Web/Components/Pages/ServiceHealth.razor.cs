using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Web.Components.Pages;

public partial class ServiceHealth
{
    [CascadingParameter] private HttpContext HttpContext { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "service")] public Guid? SelectedService { get; set; }
    [SupplyParameterFromQuery(Name = "target")] public Guid? SelectedTarget { get; set; }
    [SupplyParameterFromQuery(Name = "services")] public int? ServicePage { get; set; }
    [SupplyParameterFromQuery(Name = "page")] public int? TargetPage { get; set; }
    [SupplyParameterFromQuery(Name = "instances")] public int? InstancePage { get; set; }
    [SupplyParameterFromForm(FormName = "health-networks")] private NetworkEditor NetworkForm { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "health-target")] private TargetEditor TargetForm { get; set; } = default!;

    private IReadOnlyList<ManagedServiceSummary> services = [];
    private IReadOnlyList<ServiceInstanceSummary> instances = [];
    private IReadOnlyList<HealthTargetSnapshot> targets = [];
    private IReadOnlyList<HealthCheckObservation> observations = [];
    private IReadOnlyDictionary<Guid, ServiceInstanceSummary> targetInstances = new Dictionary<Guid, ServiceInstanceSummary>();
    private ManagedServiceSummary? selectedService;
    private HealthTargetSnapshot? editingTarget;
    private bool loadError, networkEmpty;
    private string? error, message;
    private int servicePage, targetPage, instancePage;

    protected override async Task OnInitializedAsync()
    {
        NetworkForm ??= new();
        TargetForm ??= new();
        servicePage = Math.Clamp(ServicePage ?? 1, 1, 1000);
        targetPage = Math.Clamp(TargetPage ?? 1, 1, 1000);
        instancePage = Math.Clamp(InstancePage ?? 1, 1, 1000);
        HttpContext.Response.Headers.CacheControl = "no-store";
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var token = HttpContext.RequestAborted;
            var handler = HttpMethods.IsPost(HttpContext.Request.Method) && HttpContext.Request.HasFormContentType
                ? (await HttpContext.Request.ReadFormAsync(token))["_handler"].ToString() : "";
            var policy = await Health.GetNetworksAsync(token);
            networkEmpty = policy.Networks.Count == 0;
            if (handler != "health-networks") NetworkForm = new() { Text = string.Join('\n', policy.Networks), Version = policy.Version };
            services = await Applications.ListServicesAsync(servicePage, token);
            if (SelectedService is not { } selected || selected.Version != 7) return;
            selectedService = await Applications.GetServiceAsync(selected, token);
            if (selectedService is null) { error = L["找不到選取的服務。"].Value; return; }
            targets = await Health.ListTargetsAsync(selected, targetPage, token);
            observations = await Health.LatestAsync(selected, targetPage, token);
            instances = await Instances.ListAsync(selected, instancePage, token);
            if (SelectedTarget is { } id)
            {
                editingTarget = await Health.GetTargetAsync(id, token);
                if (editingTarget is null || editingTarget.ServiceId != selected) { error = L["找不到此服務的健康檢查。"].Value; selectedService = null; return; }
                if (handler != "health-target") TargetForm = new() { Id = editingTarget.Id, InstanceId = editingTarget.InstanceId, Endpoint = editingTarget.Endpoint,
                    IntervalSeconds = editingTarget.IntervalSeconds, TimeoutSeconds = editingTarget.TimeoutSeconds,
                    FailureThreshold = editingTarget.FailureThreshold, RecoveryThreshold = editingTarget.RecoveryThreshold,
                    Enabled = editingTarget.Enabled, Version = editingTarget.Version };
            }
            var ids = targets.Where(x => x.InstanceId is not null).Select(x => x.InstanceId!.Value)
                .Concat(editingTarget?.InstanceId is { } editingId ? [editingId] : []).Distinct().ToArray();
            targetInstances = (await Instances.GetInstancesAsync(selected, ids, token)).ToDictionary(x => x.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not load health settings: {ExceptionType}", ex.GetType().Name);
            loadError = true;
        }
    }

    private async Task SaveNetworksAsync()
    {
        if (Actor() is not { } actor) { error = L["無法確認管理員身分。"].Value; return; }
        try
        {
            var values = (NetworkForm.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var saved = await Health.UpdateNetworksAsync(new(values, NetworkForm.Version), actor, HttpContext.RequestAborted);
            if (saved is null) error = L["網段設定已變更，請重新整理後再試。"].Value;
            else { NetworkForm.Version = saved.Version; networkEmpty = saved.Networks.Count == 0; message = L["網段設定已儲存。"].Value; }
        }
        catch (ArgumentException) { error = L["請逐行輸入有效的 IP 位址或 CIDR 網段，最多 64 筆。"].Value; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Logger.LogWarning("Could not save health network policy: {ExceptionType}", ex.GetType().Name); error = L["無法確認儲存結果，請重新整理後檢查。"].Value; }
        await LoadAsync();
    }

    private async Task SaveTargetAsync()
    {
        if (SelectedService is not { } selected || selected.Version != 7 || Actor() is not { } actor)
        { error = L["請先選擇服務。"].Value; return; }
        try
        {
            var input = new HealthTargetInput(TargetForm.Endpoint, TargetForm.IntervalSeconds, TargetForm.TimeoutSeconds,
                TargetForm.FailureThreshold, TargetForm.RecoveryThreshold, TargetForm.Enabled, TargetForm.Version);
            if (TargetForm.Id is { } existingId && (await Health.GetTargetAsync(existingId, HttpContext.RequestAborted))?.ServiceId != selected)
            { error = L["找不到此服務的健康檢查。"].Value; return; }
            var result = TargetForm.Id is { } id
                ? await Health.UpdateAsync(id, input, actor, HttpContext.RequestAborted)
                : await Health.CreateAsync(new(selected, TargetForm.InstanceId, input), actor, HttpContext.RequestAborted);
            if (result.Status == HealthTargetStatus.Saved)
            {
                TargetForm.Version = result.Target!.Version;
                message = L["健康檢查設定已儲存；結果會在下次檢查後顯示。"].Value;
                if (TargetForm.Id is null) TargetForm = new();
            }
            else error = result.Status switch
            {
                HealthTargetStatus.Invalid => L["請檢查網址、間隔、逾時及門檻設定；逾時不可大於檢查間隔。"].Value,
                HealthTargetStatus.Conflict => L["設定已被修改，請重新整理後再試。"].Value,
                HealthTargetStatus.Unavailable => L["服務、來源或執行個體目前不可使用，無法啟用檢查。"].Value,
                HealthTargetStatus.LimitReached => L["此服務的健康檢查已達上限。"].Value,
                _ => L["找不到服務或執行個體，請重新整理後再試。"].Value
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Logger.LogWarning("Could not save health target: {ExceptionType}", ex.GetType().Name); error = L["無法確認儲存結果，請重新整理後檢查。"].Value; }
        await LoadAsync();
    }

    private Guid? Actor() => Guid.TryParse(HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) ? actor : null;
    private static string ServiceUrl(Guid? service, int services, int page, int instances) =>
        $"/settings/service-health?service={service:D}&services={services}&page={page}&instances={instances}";
    private static string TargetUrl(Guid service, Guid target, int services, int page, int instances) =>
        $"/settings/service-health?service={service:D}&target={target:D}&services={services}&page={page}&instances={instances}";
    private string ObservationText(HealthCheckObservationKind kind) => kind switch
    {
        HealthCheckObservationKind.Healthy => L["回報正常"].Value, HealthCheckObservationKind.Degraded => L["回報降級"].Value,
        HealthCheckObservationKind.Unhealthy => L["回報異常"].Value, HealthCheckObservationKind.Timeout => L["檢查逾時"].Value,
        HealthCheckObservationKind.TransportError => L["連線失敗"].Value, HealthCheckObservationKind.InvalidResponse => L["回應格式不符"].Value,
        HealthCheckObservationKind.NetworkDenied => L["網段未允許"].Value, _ => L["未知"].Value
    };
    private string TargetSubject(Guid? instanceId) => instanceId is null ? L["整個服務"].Value :
        targetInstances.TryGetValue(instanceId.Value, out var instance) ? instance.ExternalInstanceId : L["無法讀取執行個體"].Value;

    private sealed class NetworkEditor { public string Text { get; set; } = ""; public string Version { get; set; } = ""; }
    private sealed class TargetEditor
    {
        public Guid? Id { get; set; }
        public Guid? InstanceId { get; set; }
        public string Endpoint { get; set; } = "";
        public int IntervalSeconds { get; set; } = 30;
        public int TimeoutSeconds { get; set; } = 5;
        public int FailureThreshold { get; set; } = 3;
        public int RecoveryThreshold { get; set; } = 2;
        public bool Enabled { get; set; }
        public string Version { get; set; } = "";
    }
}

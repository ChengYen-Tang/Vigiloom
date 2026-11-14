using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Services;
using Vigiloom.Web.ServiceDetails;

namespace Vigiloom.Web.Components.Pages;

public partial class ServiceDeployments
{
    [CascadingParameter] private HttpContext HttpContext { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "service")] public Guid? ServiceId { get; set; }
    [SupplyParameterFromQuery(Name = "links")] public int? LinksPage { get; set; }
    [SupplyParameterFromQuery(Name = "devices")] public int? DevicesPage { get; set; }
    [SupplyParameterFromQuery(Name = "instances")] public int? InstancesPage { get; set; }
    [SupplyParameterFromForm(FormName = "deployment-create")] private CreateEditor CreateForm { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "deployment-end")] private EndEditor EndForm { get; set; } = default!;
    private ManagedServiceSummary? selected;
    private DeploymentLinkPage? links;
    private IReadOnlyList<Vigiloom.Contracts.Devices.DeviceDetails> devices = [];
    private IReadOnlyList<ServiceInstanceSummary> instances = [];
    private int linkPage, devicePage, instancePage;
    private bool moreDevices;
    private string? error, message;

    protected override async Task OnInitializedAsync()
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        CreateForm ??= new(); EndForm ??= new();
        linkPage = Math.Clamp(LinksPage ?? 1, 1, 1000);
        devicePage = Math.Clamp(DevicesPage ?? 1, 1, 1000);
        instancePage = Math.Clamp(InstancesPage ?? 1, 1, 1000);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (ServiceId is not { } id || id.Version != 7) { error = "請從服務詳情選擇服務。"; return; }
        try
        {
            var token = HttpContext.RequestAborted;
            selected = await Instances.GetServiceAsync(id, token);
            if (selected is null) { error = "找不到選取的服務。"; return; }
            links = await Links.ListAsync(id, linkPage, token);
            instances = await Instances.ListAsync(id, instancePage, token);
            var page = await Devices.ListAsync(null, null, false, false, devicePage, token);
            devices = page.Items;
            moreDevices = devicePage < 1000 && (long)devicePage * page.PageSize < page.Total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Logger.LogWarning("Could not load deployment relationships: {ExceptionType}", ex.GetType().Name); error = "無法讀取部署關聯，請稍後再試。"; }
    }

    private async Task CreateAsync(EditContext _)
    {
        if (ServiceId is not { } service || Actor() is not { } actor || !Guid.TryParse(CreateForm.DeviceId, out var device) || !Guid.TryParse(CreateForm.InstanceId, out var instance))
        { error = "請選擇主機與執行個體。"; return; }
        try
        {
            var result = await Links.CreateAsync(service, new(device, instance, CreateForm.Evidence ?? ""), actor, HttpContext.RequestAborted);
            if (result.Status == DeploymentLinkStatus.Saved) { message = "部署關聯已建立。"; CreateForm = new(); }
            else error = Failure(result.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Logger.LogWarning("Could not create deployment relationship: {ExceptionType}", ex.GetType().Name); error = "無法確認儲存結果，請重新整理後檢查。"; }
        await LoadAsync();
    }

    private async Task EndAsync(EditContext _)
    {
        var parts = (EndForm.Selection ?? "").Split('|');
        if (ServiceId is not { } service || Actor() is not { } actor || parts.Length != 2 || !Guid.TryParse(parts[0], out var id))
        { error = "請選擇要結束的關聯。"; return; }
        try
        {
            var result = await Links.EndAsync(service, id, new(parts[1]), actor, HttpContext.RequestAborted);
            if (result == DeploymentLinkStatus.Saved) message = "部署關聯已結束。";
            else error = Failure(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Logger.LogWarning("Could not end deployment relationship: {ExceptionType}", ex.GetType().Name); error = "無法確認儲存結果，請重新整理後檢查。"; }
        await LoadAsync();
    }

    private Guid? Actor() => Guid.TryParse(HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) ? actor : null;
    private string PageUrl(int link, int device, int instance) => $"/settings/service-deployments?service={ServiceId:D}&links={link}&devices={device}&instances={instance}";
    private static string Failure(DeploymentLinkStatus status) => status switch
    {
        DeploymentLinkStatus.Invalid => "請提供有效的主機、執行個體與確認依據（最多 512 字）。",
        DeploymentLinkStatus.NotFound => "找不到設備或執行個體，請重新整理。",
        _ => "關聯或資源狀態已變更，請重新整理並確認。"
    };
    private sealed class CreateEditor { public string? DeviceId { get; set; } public string? InstanceId { get; set; } public string? Evidence { get; set; } }
    private sealed class EndEditor { public string? Selection { get; set; } }
}

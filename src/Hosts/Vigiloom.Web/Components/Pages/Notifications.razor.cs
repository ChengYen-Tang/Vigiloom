using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Notifications;

namespace Vigiloom.Web.Components.Pages;

public partial class Notifications
{
    [CascadingParameter] private HttpContext HttpContext { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "source")] public string? Source { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [SupplyParameterFromQuery(Name = "to")] public string? To { get; set; }
    [SupplyParameterFromQuery(Name = "page")] public string? Page { get; set; }
    private NotificationPage? result;
    private string? error;

    protected override async Task OnParametersSetAsync()
    {
        result = null;
        error = null;
        if (!string.IsNullOrEmpty(Source) && Source is not ("ping" or "service-health" or "sigma" or "vulnerability"))
        { error = "請選擇有效的通知來源。"; return; }
        if (!TryDate(From, out var from) || !TryDate(To, out var to))
        { error = "請選擇有效的日期。"; return; }
        if (from > to) { error = "開始日期不能晚於結束日期。"; return; }
        var page = 1;
        if (Page is not null && (!int.TryParse(Page, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page is < 1 or > 1000))
        { error = "頁碼無效，請重新查詢。"; return; }
        try { result = await Reader.GetRecentAsync(new NotificationQuery(page, Source, from, to), HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Notification query failed: {ExceptionType}", ex.GetType().Name);
            error = "無法載入通知紀錄，請稍後再試。";
        }
    }

    private static bool TryDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value)) return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }

    private static string SourceLabel(string source) => source switch
    {
        "ping" => "Ping", "service-health" => "服務健康", "sigma" => "行為規則", "vulnerability" => "漏洞評估", _ => source
    };

    private static string? ResourceLink(NotificationView entry) => entry.ResourceId is not { } id ? null : entry.Source switch
    {
        "service-health" => $"/services/{id:D}",
        "ping" or "sigma" or "vulnerability" => $"/devices/{id:D}",
        _ => null
    };

    private string PageUrl(int page) => QueryHelpers.AddQueryString("/notifications", new Dictionary<string, string?>
    {
        ["source"] = Source, ["from"] = From, ["to"] = To, ["page"] = page.ToString(CultureInfo.InvariantCulture)
    });
}

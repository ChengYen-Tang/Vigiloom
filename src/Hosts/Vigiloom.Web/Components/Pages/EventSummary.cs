using Microsoft.Extensions.Localization;
using Vigiloom.Contracts.Events;

namespace Vigiloom.Web.Components.Pages;

/// <summary>Localizes known system labels without changing event data.</summary>
internal static class EventSummary
{
    public static string Format(EventTimelineEntry entry, IStringLocalizer<EventText> text)
    {
        string[] labels = entry.Source switch
        {
            "host-log" => ["主機日誌採集缺口", "主機日誌"],
            "service-log" => ["應用程式日誌"],
            "service-trace" => ["應用程式追蹤"],
            "service-health" => ["服務健康檢查"],
            "inventory" => ["資產盤點變更"],
            "integrity" => ["完整性變更"],
            "sigma" => ["行為規則後續命中", "行為規則命中", "行為規則未評估"],
            "vulnerability" => ["漏洞評估"],
            "baseline" => ["安全基準檢查"],
            "ioc" => ["IOC 比對線索"],
            "yara" => ["YARA 掃描"],
            _ => []
        };
        foreach (var label in labels)
        {
            if (entry.Summary == label) return text[label];
            var prefix = label + "：";
            if (entry.Summary.StartsWith(prefix, StringComparison.Ordinal))
                return text["{0}：{1}", text[label].Value, entry.Summary[prefix.Length..]];
        }
        return entry.Summary;
    }
}

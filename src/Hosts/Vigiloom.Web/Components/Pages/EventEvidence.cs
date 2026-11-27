using System.Text.RegularExpressions;
using Microsoft.Extensions.Localization;
using Vigiloom.Contracts.Events;

namespace Vigiloom.Web.Components.Pages;

/// <summary>Formats recognized system evidence while preserving evidence values verbatim.</summary>
internal static class EventEvidence
{
    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private const string Field = "[^；]*";

    public static string Format(EventTimelineEntry entry, IStringLocalizer<EventText> text)
    {
        var guid = $"({GuidPattern})";
        var field = $"({Field})";
        (string Pattern, string Template)[] formats = entry.Source switch
        {
            "host-log" => [
                ($"解析版本 ([0-9]+)；{guid}/{guid}；原文：([\\s\\S]*)", "解析版本 {0}；{1}/{2}；原文：{3}"),
                ($"解析版本 ([0-9]+)；{guid}/{guid}", "解析版本 {0}；{1}/{2}")],
            "service-log" => [( $"服務實例 ({GuidPattern}|未指定)", "服務實例 {0}")],
            "service-health" => [($"檢查 {guid}", "檢查 {0}")],
            "yara" => [($"掃描 {guid}；結果 {field}", "掃描 {0}；結果 {1}")],
            "inventory" => [($"來源 {field}；類型 {field}；Agent {guid}；世代 {field}", "來源 {0}；類型 {1}；Agent {2}；世代 {3}")],
            "integrity" => [
                ($"範圍 {guid}；版本 {field}；類型 {field}；Agent {guid}；採集有缺口", "範圍 {0}；版本 {1}；類型 {2}；Agent {3}；採集有缺口"),
                ($"範圍 {guid}；版本 {field}；類型 {field}；Agent {guid}", "範圍 {0}；版本 {1}；類型 {2}；Agent {3}")],
            "sigma" => [
                ($"先前未評估；版本 {guid}；事件 {guid}/{guid}", "先前未評估；版本 {0}；事件 {1}/{2}"),
                ($"版本 {guid}；事件 {guid}/{guid}", "版本 {0}；事件 {1}/{2}")],
            "ioc" => [($"版本 {guid}；事件 {guid}/{guid}", "版本 {0}；事件 {1}/{2}")],
            "vulnerability" => [($"情報版本 {guid}；狀態 {field}", "情報版本 {0}；狀態 {1}")],
            "baseline" => [($"版本 {guid}；結果 {field}", "版本 {0}；結果 {1}")],
            _ => []
        };
        foreach (var (pattern, template) in formats)
        {
            var match = Regex.Match(entry.Evidence, $"\\A{pattern}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success) continue;
            var values = match.Groups.Values.Skip(1).Select(group => (object)group.Value).ToArray();
            if (entry.Source == "service-log" && values[0] is "未指定") values[0] = text["未指定"].Value;
            return text[template, values];
        }
        return entry.Evidence;
    }
}

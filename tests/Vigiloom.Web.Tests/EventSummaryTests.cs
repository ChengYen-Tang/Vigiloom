using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Vigiloom.Contracts.Events;
using Vigiloom.Web.Components.Pages;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class EventSummaryTests
{
    [TestMethod]
    [DataRow("en", "Application logs: 來源：原文 版本")]
    [DataRow("zh-Hans", "应用程序日志：來源：原文 版本")]
    public void LocalizesOnlySystemLabel(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var localizer = services.GetRequiredService<IStringLocalizer<EventText>>();
            var entry = new EventTimelineEntry("service-log", DateTimeOffset.UtcNow, Guid.Empty, "service",
                "應用程式日誌：來源：原文 版本", "", "/");

            Assert.AreEqual(expected, EventSummary.Format(entry, localizer));
            Assert.AreEqual("未知：原文", EventSummary.Format(entry with { Summary = "未知：原文" }, localizer));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("en", "Parser version 2; 11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222; Original: 版本；原文：不變\n來源")]
    [DataRow("zh-Hans", "解析版本 2；11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222；原文：版本；原文：不變\n來源")]
    public void LocalizesEvidenceWithoutModifyingOriginalLog(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var localizer = services.GetRequiredService<IStringLocalizer<EventText>>();
            var entry = new EventTimelineEntry("host-log", DateTimeOffset.UtcNow, Guid.Empty, "device", "",
                "解析版本 2；11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222；原文：版本；原文：不變\n來源", "/");

            Assert.AreEqual(expected, EventEvidence.Format(entry, localizer));
            Assert.AreEqual("解析版本 x；自訂資料", EventEvidence.Format(entry with { Evidence = "解析版本 x；自訂資料" }, localizer));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("service-log", "服務實例 未指定", "Service instance Unspecified")]
    [DataRow("service-health", "檢查 11111111-1111-1111-1111-111111111111", "Check 11111111-1111-1111-1111-111111111111")]
    [DataRow("yara", "掃描 11111111-1111-1111-1111-111111111111；結果 1", "Scan 11111111-1111-1111-1111-111111111111; Result 1")]
    [DataRow("baseline", "版本 11111111-1111-1111-1111-111111111111；結果 Passed", "Version 11111111-1111-1111-1111-111111111111; Result Passed")]
    [DataRow("vulnerability", "情報版本 11111111-1111-1111-1111-111111111111；狀態 2", "Intelligence version 11111111-1111-1111-1111-111111111111; Status 2")]
    [DataRow("service-trace", "Trace abc；Span def", "Trace abc；Span def")]
    public void LocalizesKnownEvidenceFormats(string source, string evidence, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var localizer = services.GetRequiredService<IStringLocalizer<EventText>>();
            var entry = new EventTimelineEntry(source, DateTimeOffset.UtcNow, Guid.Empty, "device", "", evidence, "/");

            Assert.AreEqual(expected, EventEvidence.Format(entry, localizer));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}

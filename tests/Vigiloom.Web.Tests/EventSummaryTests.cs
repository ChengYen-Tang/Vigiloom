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
}

using Vigiloom.Modules.Publishing.Domain;

namespace Vigiloom.Modules.Publishing.Tests;

[TestClass]
public sealed class TemplateHistoryTests
{
    [TestMethod]
    public void HistoryPeriod_OnlyRestartsWhenEnabledOrExpanded()
    {
        var now = DateTimeOffset.UtcNow;
        var template = new PublicationTemplate("Example", true, true, now);
        template.Update("Example", true, true, now, 7);
        Assert.AreEqual(PublicationTemplate.PendingHistorySince, template.HistorySince);
        template.ConfirmHistorySince(now);
        template.Update("Renamed", true, true, now);
        Assert.AreEqual(7, template.HistoryDays);
        Assert.AreEqual(now, template.HistorySince);
        template.Update("Renamed", true, true, now, 1);
        Assert.AreEqual(now, template.HistorySince);
        template.Update("Renamed", true, true, now, 7);
        Assert.AreEqual(PublicationTemplate.PendingHistorySince, template.HistorySince);
        template.Update("Renamed", true, true, now, 0);
        Assert.IsNull(template.HistorySince);
        template.Update("Renamed", true, true, now, 1);
        Assert.AreEqual(PublicationTemplate.PendingHistorySince, template.HistorySince);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => template.Update("Changed", false, false, now, 8));
        Assert.AreEqual("Renamed", template.Name);
        Assert.AreEqual(1, template.HistoryDays);
    }
}

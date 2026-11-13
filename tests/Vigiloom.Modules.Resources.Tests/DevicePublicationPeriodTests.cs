using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
public sealed class DevicePublicationPeriodTests
{
    [TestMethod]
    public void PublicationPeriod_TracksRevocationAndExplicitRestart()
    {
        var start = DateTimeOffset.UtcNow;
        var device = new Device("Router", "router.example", false, start);
        device.SetPublication(true, "Network", PublicationPolicy.BaselineVersion, start);
        Assert.AreEqual(start, device.PublicSince);
        var template = Guid.CreateVersion7();
        device.SetTemplatePublication("Network", template, false, start.AddMinutes(1));
        Assert.AreEqual(start, device.PublicSince);
        Assert.AreEqual(template, device.PublicTemplateId);
        device.SetPublication(true, "Network", PublicationPolicy.BaselineVersion, start.AddMinutes(2));
        Assert.AreEqual(start, device.PublicSince);
        Assert.IsNull(device.PublicTemplateId);
        device.SetTemplatePublication("Network", template, true, start.AddMinutes(3));
        Assert.AreEqual(start.AddMinutes(3), device.PublicSince);
        device.SetPublication(false, null, null, start.AddMinutes(4));
        Assert.IsNull(device.PublicSince);
        device.SetTemplatePublication("Network", template, false, start.AddMinutes(5));
        Assert.AreEqual(start.AddMinutes(5), device.PublicSince);
        device.Archive(start.AddMinutes(6));
        Assert.IsFalse(device.IsPublic);
        Assert.IsNull(device.PublicSince);
    }
}

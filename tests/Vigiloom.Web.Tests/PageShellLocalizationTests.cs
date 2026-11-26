using System.Globalization;
using System.Resources;
using Vigiloom.Web.Components.Pages;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class PageShellLocalizationTests
{
    [TestMethod]
    [DataRow("en", "Publication settings", "Device name")]
    [DataRow("zh-Hans", "公开设置", "设备名称")]
    public void Resources_ContainPublicationLabels(string cultureName, string settings, string deviceName)
    {
        var resources = new ResourceManager(typeof(PageShellText));
        var culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.AreEqual(settings, resources.GetString("公開設定", culture));
        Assert.AreEqual(deviceName, resources.GetString("設備名稱", culture));
    }
}

using System.Globalization;
using System.Resources;
using Vigiloom.Web.Client.Components.AgentVersions;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class AgentVersionLocalizationTests
{
    [TestMethod]
    [DataRow("en", "Assign an Agent package", "Unable to save the assignment. Try again later.")]
    [DataRow("zh-Hans", "指派 Agent 包", "无法保存指派，请稍后重试。")]
    public void AgentVersionResources_ContainAssignmentAndErrorMessages(string cultureName, string assignment, string error)
    {
        var resources = new ResourceManager(typeof(VersionText));
        var culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.AreEqual(assignment, resources.GetString("指派 Agent 套件", culture));
        Assert.AreEqual(error, resources.GetString("無法儲存指派，請稍後重試。", culture));
    }
}

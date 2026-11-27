using System.Globalization;
using System.Resources;
using Vigiloom.Web.Client.Components.Publishing;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class PublicationLocalizationTests
{
    [TestMethod]
    [DataRow("en", "Private (visible after sign-in)", "Your session expired or permissions changed. Please sign in again.")]
    [DataRow("zh-Hans", "不公开（仅登录后可查看）", "登录已过期或权限已更改，请重新登录。")]
    public void Resources_IncludeVisibilityAndAuthorizationMessages(string cultureName, string privateLabel, string error)
    {
        var resources = new ResourceManager(typeof(PublicationText));
        var culture = CultureInfo.GetCultureInfo(cultureName);
        Assert.AreEqual(privateLabel, resources.GetString("不公開（僅登入後可查看）", culture));
        Assert.AreEqual(error, resources.GetString("登入已過期或權限已變更，請重新登入。", culture));
    }
}

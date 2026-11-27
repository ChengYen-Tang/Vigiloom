using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Components.Account;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class AccountValidationMessagesTests
{
    [TestMethod]
    [DataRow("en", "The username is already in use.")]
    [DataRow("zh-Hant", "帳號已存在。")]
    [DataRow("zh-Hans", "账号已存在。")]
    public void LocalizesIdentityErrorsAfterTransport(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<IStringLocalizer<SetupText>>();
            var original = AccountOperationResult.Failed(IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateUserName", Description = "Original service message"
            }));
            var received = JsonSerializer.Deserialize<AccountOperationResult>(JsonSerializer.Serialize(original))!;
            Assert.AreEqual(expected, AccountOperationMessages.DescribeValidation(received, text));
            Assert.AreEqual("Original service message", AccountOperationMessages.DescribeValidation(received with { ErrorCodes = null }, text));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}

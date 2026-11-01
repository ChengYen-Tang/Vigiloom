using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OtpNet;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Web.Authentication;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TwoFactorHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task InvalidSecondFactorCodes_TriggerAccountLockout(bool recovery) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string secret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString, ["Vigiloom:SetupToken"] = secret
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            string validCode;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setup = scope.ServiceProvider.GetRequiredService<InitialAdministrator>();
                Assert.AreEqual(InitialAdministratorStatus.Created, (await setup.CreateAsync(secret, "admin", "Strong-Password-123", token)).Status);
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await manager.FindByNameAsync("admin");
                Assert.IsNotNull(account);
                Assert.IsTrue((await manager.ResetAuthenticatorKeyAsync(account)).Succeeded);
                var key = await manager.GetAuthenticatorKeyAsync(account);
                Assert.IsNotNull(key);
                validCode = new Totp(Base32Encoding.ToBytes(key)).ComputeTotp();
                Assert.IsTrue((await manager.SetTwoFactorEnabledAsync(account, true)).Succeeded);
            }
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            async Task<HttpResponseMessage> Post(string url, string formName, string field, string value)
            {
                using var page = await client.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
                if (formName == "login") { fields["Input.UserName"] = "admin"; fields["Input.Password"] = "Strong-Password-123"; }
                else fields[field] = value;
                return await client.PostAsync(url, new FormUrlEncodedContent(fields), token);
            }
            using var login = await Post("/account/login", "login", "", "");
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            Assert.IsNotNull(login.Headers.Location);
            var challenge = login.Headers.Location.PathAndQuery;
            var invalid = validCode == "000000" ? "111111" : "000000";
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await Post(challenge, recovery ? "recovery-login" : "totp-login",
                    recovery ? "RecoveryInput.Code" : "TotpInput.Code", invalid);
                Assert.AreEqual(HttpStatusCode.OK, failed.StatusCode);
            }
            await using var verify = factory.Services.CreateAsyncScope();
            var userManager = verify.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var user = await userManager.FindByNameAsync("admin");
            Assert.IsNotNull(user);
            Assert.IsTrue(await userManager.IsLockedOutAsync(user));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task AuthenticatorAndRecoveryCode_WorkThroughProtectedForms() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = setupSecret,
                    ["Vigiloom:Authentication:RequestsPerMinute"] = "100"
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            async Task<Dictionary<string, string>> Form(string url, string formName)
            {
                using var response = await client.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
                return form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            }
            async Task<HttpResponseMessage> Post(string url, string formName, params (string Key, string Value)[] values)
            {
                var fields = await Form(url, formName);
                foreach (var (key, value) in values) fields[key] = value;
                return await client.PostAsync(url, new FormUrlEncodedContent(fields), token);
            }
            async Task Logout()
            {
                using var response = await Post("/account/logout", "logout");
                Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
            }
            async Task<string> BeginLogin()
            {
                using var response = await Post("/account/login", "login", ("Input.UserName", "admin"), ("Input.Password", "Strong-Password-123"));
                Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
                Assert.IsNotNull(response.Headers.Location);
                Assert.AreEqual("/account/two-factor", response.Headers.Location.AbsolutePath);
                var cookieName = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get(IdentityConstants.TwoFactorUserIdScheme).Cookie.Name;
                var temporaryCookies = response.Headers.GetValues("Set-Cookie")
                    .Where(value => value.StartsWith(cookieName + "=", StringComparison.Ordinal)).ToArray();
                Assert.HasCount(2, temporaryCookies, $"Cookie names: {string.Join(", ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split('=')[0]))}; expected: {cookieName}");
                using var unboundClient = factory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost")
                });
                using var unboundRequest = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location.PathAndQuery);
                unboundRequest.Headers.Add("Cookie", temporaryCookies[0].Split(';')[0]);
                using var unbound = await unboundClient.SendAsync(unboundRequest, token);
                Assert.AreEqual(HttpStatusCode.Redirect, unbound.StatusCode);
                return response.Headers.Location.PathAndQuery;
            }
            using (var setup = await Post("/account/setup", "setup", ("Input.SetupToken", setupSecret), ("Input.UserName", "admin"),
                ("Input.Password", "Strong-Password-123"), ("Input.ConfirmPassword", "Strong-Password-123"))) Assert.AreEqual(HttpStatusCode.Redirect, setup.StatusCode);
            using (var login = await Post("/account/login", "login", ("Input.UserName", "admin"), ("Input.Password", "Strong-Password-123"))) Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            using var begin = await Post("/account/two-factor-settings", "begin-two-factor", ("BeginInput.Password", "Strong-Password-123"));
            Assert.AreEqual(HttpStatusCode.OK, begin.StatusCode);
            Assert.IsTrue(begin.Headers.CacheControl?.NoStore);
            var beginDocument = await new HtmlParser().ParseDocumentAsync(await begin.Content.ReadAsStringAsync(token), token);
            var key = beginDocument.QuerySelector(".account-qr code")?.TextContent;
            Assert.IsFalse(string.IsNullOrWhiteSpace(key));
            Assert.StartsWith("data:image/png;base64,", beginDocument.QuerySelector(".account-qr img")?.GetAttribute("src") ?? "");
            var totp = new Totp(Base32Encoding.ToBytes(key!));
            using var enabled = await Post("/account/two-factor-settings", "enable-two-factor", ("ConfirmInput.Code", totp.ComputeTotp()));
            Assert.AreEqual(HttpStatusCode.OK, enabled.StatusCode);
            var enabledDocument = await new HtmlParser().ParseDocumentAsync(await enabled.Content.ReadAsStringAsync(token), token);
            var recovery = enabledDocument.QuerySelector("textarea[aria-label='雙重驗證復原碼']")?.TextContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.IsNotNull(recovery);
            Assert.HasCount(10, recovery);
            await Logout();
            var challenge = await BeginLogin();
            var staleFields = await Form(challenge, "totp-login");
            staleFields["TotpInput.Code"] = totp.ComputeTotp();
            await using (var rotate = factory.Services.CreateAsyncScope())
            {
                var manager = rotate.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await manager.FindByNameAsync("admin");
                Assert.IsNotNull(account);
                Assert.IsTrue((await manager.UpdateSecurityStampAsync(account)).Succeeded);
            }
            using (var stale = await client.PostAsync(challenge, new FormUrlEncodedContent(staleFields), token))
                Assert.AreNotEqual("/", stale.Headers.Location?.OriginalString);
            using (var protectedPage = await client.GetAsync("/account/two-factor-settings", token))
                Assert.AreEqual(HttpStatusCode.Redirect, protectedPage.StatusCode);
            challenge = await BeginLogin();
            using (var totpLogin = await Post(challenge, "totp-login", ("TotpInput.Code", totp.ComputeTotp()))) { Assert.AreEqual(HttpStatusCode.Redirect, totpLogin.StatusCode); Assert.AreEqual("/", totpLogin.Headers.Location?.AbsolutePath); }
            await Logout();
            challenge = await BeginLogin();
            using (var recoveryLogin = await Post(challenge, "recovery-login", ("RecoveryInput.Code", recovery[0]))) { Assert.AreEqual(HttpStatusCode.Redirect, recoveryLogin.StatusCode); Assert.AreEqual("/", recoveryLogin.Headers.Location?.AbsolutePath); }
            await Logout();
            challenge = await BeginLogin();
            using (var reused = await Post(challenge, "recovery-login", ("RecoveryInput.Code", recovery[0]))) Assert.AreEqual(HttpStatusCode.OK, reused.StatusCode);
            using (var retry = await Post(challenge, "totp-login", ("TotpInput.Code", totp.ComputeTotp()))) { Assert.AreEqual(HttpStatusCode.Redirect, retry.StatusCode); Assert.AreEqual("/", retry.Headers.Location?.AbsolutePath); }
            using (var disabled = await Post("/account/two-factor-settings", "disable-two-factor", ("DisableInput.Password", "Strong-Password-123"),
                ("DisableInput.Code", totp.ComputeTotp()))) Assert.AreEqual(HttpStatusCode.OK, disabled.StatusCode, await disabled.Content.ReadAsStringAsync(token));
            await Logout();
            using var finalLogin = await Post("/account/login", "login", ("Input.UserName", "admin"), ("Input.Password", "Strong-Password-123"));
            Assert.AreEqual(HttpStatusCode.Redirect, finalLogin.StatusCode);
            Assert.AreEqual("/", finalLogin.Headers.Location?.AbsolutePath);
        }, TestContext.CancellationToken);
}

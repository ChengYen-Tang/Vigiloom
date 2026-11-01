using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InitialAdministratorTests
{
    private const string Token = "test-only-bootstrap-secret-with-adequate-length";
    public TestContext TestContext { get; set; } = null!;

    [AssemblyCleanup]
    public static void VerifyRunCleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    public Task SetupAndLogin_RequireAntiforgeryAndRejectExternalRedirects() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = Token
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var missingAntiforgery = await client.PostAsync("/account/setup", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "setup", ["Input.SetupToken"] = Token, ["Input.UserName"] = "admin",
                ["Input.Password"] = "Strong-Password-123", ["Input.ConfirmPassword"] = "Strong-Password-123"
            }), token);
            Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

            async Task<Dictionary<string, string>> HiddenFields(string url)
            {
                using var response = await client.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                return document.QuerySelectorAll("form input[type=hidden][name]")
                    .ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            }
            var setup = await HiddenFields("/account/setup");
            setup["Input.SetupToken"] = Token;
            setup["Input.UserName"] = "admin";
            setup["Input.Password"] = "Strong-Password-123";
            setup["Input.ConfirmPassword"] = "Strong-Password-123";
            using var setupResponse = await client.PostAsync("/account/setup", new FormUrlEncodedContent(setup), token);
            Assert.AreEqual(HttpStatusCode.Redirect, setupResponse.StatusCode, await setupResponse.Content.ReadAsStringAsync(token));
            Assert.AreEqual("/account/login?setup=complete", setupResponse.Headers.Location?.PathAndQuery);

            const string loginUrl = "/account/login?ReturnUrl=https%3A%2F%2Fevil.example";
            async Task<string> BadLogin(string name, string password)
            {
                var form = await HiddenFields(loginUrl);
                form["Input.UserName"] = name;
                form["Input.Password"] = password;
                using var response = await client.PostAsync(loginUrl, new FormUrlEncodedContent(form), token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                return document.Body!.TextContent;
            }
            const string genericError = "帳號或密碼錯誤，或帳號暫時無法登入。";
            Assert.Contains(genericError, await BadLogin("missing-user", "Wrong-password-123"));
            for (var attempt = 0; attempt < 5; attempt++)
                Assert.Contains(genericError, await BadLogin("admin", "Wrong-password-123"));
            Assert.Contains(genericError, await BadLogin("admin", "Strong-Password-123"));
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await userManager.FindByNameAsync("admin");
                Assert.IsNotNull(account);
                Assert.IsTrue(await userManager.IsLockedOutAsync(account));
                Assert.IsTrue((await userManager.SetLockoutEndDateAsync(account, null)).Succeeded);
            }
            var login = await HiddenFields(loginUrl);
            login["Input.UserName"] = "admin";
            login["Input.Password"] = "Strong-Password-123";
            using var loginResponse = await client.PostAsync(loginUrl, new FormUrlEncodedContent(login), token);
            Assert.AreEqual(HttpStatusCode.Redirect, loginResponse.StatusCode, await loginResponse.Content.ReadAsStringAsync(token));
            Assert.AreEqual("/", loginResponse.Headers.Location?.PathAndQuery);
            Assert.IsTrue(loginResponse.Headers.TryGetValues("Set-Cookie", out var cookies));
            Assert.IsTrue(cookies.Any(cookie => cookie.Contains(".AspNetCore.Identity.Application", StringComparison.Ordinal) &&
                cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)));
            var logout = await HiddenFields("/account/logout");
            using var noTokenLogout = await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "logout" }), token);
            Assert.AreEqual(HttpStatusCode.BadRequest, noTokenLogout.StatusCode);
            using var logoutResponse = await client.PostAsync("/account/logout", new FormUrlEncodedContent(logout), token);
            Assert.AreEqual(HttpStatusCode.Redirect, logoutResponse.StatusCode);
            using var unauthorized = await client.GetAsync("/account/logout", token);
            Assert.AreEqual(HttpStatusCode.Redirect, unauthorized.StatusCode);
            Assert.Contains("/account/login", unauthorized.Headers.Location?.OriginalString ?? "");
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task InitialSetup_RejectsInvalidInputsAndCreatesOnlyOneAdministratorConcurrently() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = Token
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            async Task<InitialAdministratorResult> Attempt(string? secret, string name, string password)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<InitialAdministrator>().CreateAsync(secret, name, password, token);
            }
            var invalidSecret = await Attempt("incorrect", "alice", "Strong-Password-123");
            Assert.AreEqual(InitialAdministratorStatus.InvalidSetupToken, invalidSecret.Status);
            var invalidPassword = await Attempt(Token, "alice", "short");
            Assert.AreEqual(InitialAdministratorStatus.ValidationFailed, invalidPassword.Status);
            var attempts = await Task.WhenAll(Attempt(Token, "alice", "Strong-Password-123"),
                Attempt(Token, "bob", "Strong-Password-456"));
            Assert.HasCount(1, attempts.Where(result => result.Status == InitialAdministratorStatus.Created));
            Assert.HasCount(1, attempts.Where(result => result.Status == InitialAdministratorStatus.AlreadyInitialized));
            await using var check = await contexts.CreateDbContextAsync(token);
            Assert.AreEqual(1, await check.Users.CountAsync(token));
            Assert.AreEqual(1, await check.UserRoles.CountAsync(token));
            var administrator = await check.Roles.SingleAsync(role => role.NormalizedName == "ADMINISTRATOR", token);
            Assert.AreEqual(administrator.Id, (await check.UserRoles.SingleAsync(token)).RoleId);
            await using var updateScope = factory.Services.CreateAsyncScope();
            var userManager = updateScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var roleManager = updateScope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
            var user = await userManager.FindByIdAsync((await check.Users.SingleAsync(token)).Id.ToString());
            Assert.IsNotNull(user);
            Assert.IsTrue((await userManager.UpdateSecurityStampAsync(user)).Succeeded);
            var role = await roleManager.FindByNameAsync("Administrator");
            Assert.IsNotNull(role);
            Assert.IsTrue((await roleManager.UpdateAsync(role)).Succeeded);
        }, TestContext.CancellationToken);
}

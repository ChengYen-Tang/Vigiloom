extern alias IdentityHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Globalization;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteIdentityHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task WebApi_UsesIdentityHostForCredentialAndRevocation() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            var sessionRequests = 0;
            var cookieRequests = 0;
            var revocationRequests = 0;
            var passwordRequests = 0;
            using var identity = new WebApplicationFactory<IdentityHost::Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:VigiloomIdentity"] = database.ConnectionString
                })));
            using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Services:Identity:Url"] = "http://identity"
                }));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteApiCredentialValidation))
                    .ConfigurePrimaryHttpMessageHandler(() => identity.Server.CreateHandler()));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteWorkspaceIdentity))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref sessionRequests))));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteCookieValidator))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), request =>
                    {
                        Interlocked.Increment(ref cookieRequests);
                        if (request.RequestUri?.AbsolutePath == "/internal/identity/sessions/revoke") Interlocked.Increment(ref revocationRequests);
                    })));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemotePasswordLogin))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref passwordRequests))));
            });
            using (var registrationScope = web.Services.CreateScope())
                Assert.IsInstanceOfType<RemoteApiCredentialValidation>(registrationScope.ServiceProvider.GetRequiredService<IApiCredentialValidation>());
            var contexts = web.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            var secret = RandomNumberGenerator.GetBytes(32);
            var credential = new ApiCredential("remote", SHA256.HashData(secret), true, DateTimeOffset.UtcNow.AddDays(1), Guid.CreateVersion7());
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                db.ApiCredentials.Add(credential);
                await db.SaveChangesAsync(token);
            }
            using var client = web.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(secret)}");
            using (var valid = await client.GetAsync("/api/v1/access", token))
                Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var stored = await db.ApiCredentials.SingleAsync(x => x.Id == credential.Id, token);
                stored.Revoke(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            using var revoked = await client.GetAsync("/api/v1/access", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);

            await RoleInitializer.InitializeAsync(contexts, token);
            await using var scope = web.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var user = new Account { UserName = "remote-reader" };
            Assert.IsTrue((await users.CreateAsync(user, "Reader-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(user, BuiltInRoles.ReadOnly)).Succeeded);
            using var browser = web.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using var loginPage = await browser.GetAsync("/account/login", token);
            var document = await new HtmlParser().ParseDocumentAsync(await loginPage.Content.ReadAsStringAsync(token), token);
            var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
            var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            fields["Input.UserName"] = "remote-reader";
            fields["Input.Password"] = "wrong-password";
            using (var failedLogin = await browser.PostAsync("/account/login", new FormUrlEncodedContent(fields), token))
                Assert.AreEqual(HttpStatusCode.OK, failedLogin.StatusCode);
            await using (var verifyFailure = web.Services.CreateAsyncScope())
            {
                var failedUsers = verifyFailure.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var failedAccount = await failedUsers.FindByIdAsync(user.Id.ToString("D"));
                Assert.AreEqual(1, await failedUsers.GetAccessFailedCountAsync(failedAccount!));
            }
            fields["Input.Password"] = "Reader-Password-123";
            using (var login = await browser.PostAsync("/account/login", new FormUrlEncodedContent(fields), token))
                Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            using (var authorized = await browser.GetAsync("/api/v1/workspace/identity", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
                Assert.IsTrue(authorized.Headers.Contains("Set-Cookie"));
            }
            Assert.IsGreaterThan(0, cookieRequests);
            await scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>().Entry(user).ReloadAsync(token);
            var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(options.ClaimsIdentity.UserIdClaimType, user.Id.ToString("D")),
                new Claim(options.ClaimsIdentity.SecurityStampClaimType, (await users.GetSecurityStampAsync(user))!),
                new Claim(SessionLifetime.ExpirationClaim, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
            ], "test"));
            var workspace = scope.ServiceProvider.GetRequiredService<WorkspaceIdentityService>();
            var revalidation = (IdentityRevalidatingAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
            var validSession = await workspace.GetAsync(principal, token);
            Assert.AreEqual("remote-reader", validSession!.UserName);
            Assert.Contains(BuiltInRoles.ReadOnly, validSession.Roles);
            Assert.IsTrue(await revalidation.RevalidateAsync(principal, token));
            Assert.IsTrue((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
            Assert.IsNull(await workspace.GetAsync(principal, token));
            Assert.IsTrue(await revalidation.RevalidateAsync(principal, token));
            Assert.IsTrue((await users.SetLockoutEndDateAsync(user, null)).Succeeded);
            using var logoutPage = await browser.GetAsync("/account/logout", token);
            Assert.AreEqual(HttpStatusCode.OK, logoutPage.StatusCode);
            var logoutDocument = await new HtmlParser().ParseDocumentAsync(await logoutPage.Content.ReadAsStringAsync(token), token);
            var logoutForm = logoutDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "logout");
            var logoutFields = logoutForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            using (var logout = await browser.PostAsync("/account/logout", new FormUrlEncodedContent(logoutFields), token))
                Assert.AreEqual(HttpStatusCode.Redirect, logout.StatusCode);
            Assert.AreEqual(1, revocationRequests);
            await scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>().Entry(user).ReloadAsync(token);
            using (var expiredCookie = await browser.GetAsync("/api/v1/workspace/identity", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, expiredCookie.StatusCode);
            Assert.IsNull(await workspace.GetAsync(principal, token));
            Assert.IsFalse(await revalidation.RevalidateAsync(principal, token));
            Assert.AreEqual(7, sessionRequests);

            Assert.IsTrue((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            var authenticatorKey = await users.GetAuthenticatorKeyAsync(user);
            Assert.IsFalse(string.IsNullOrEmpty(authenticatorKey));
            await using (var identityScope = identity.Services.CreateAsyncScope())
            {
                var identityUsers = identityScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var identityUser = await identityUsers.FindByIdAsync(user.Id.ToString("D"));
                Assert.AreEqual(authenticatorKey, await identityUsers.GetAuthenticatorKeyAsync(identityUser!));
            }
            Assert.IsTrue((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
            using var twoFactorBrowser = web.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using var twoFactorLoginPage = await twoFactorBrowser.GetAsync("/account/login", token);
            var loginDocument = await new HtmlParser().ParseDocumentAsync(await twoFactorLoginPage.Content.ReadAsStringAsync(token), token);
            var loginForm = loginDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
            var loginFields = loginForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            loginFields["Input.UserName"] = "remote-reader";
            loginFields["Input.Password"] = "Reader-Password-123";
            using var twoFactorLogin = await twoFactorBrowser.PostAsync("/account/login", new FormUrlEncodedContent(loginFields), token);
            Assert.AreEqual(HttpStatusCode.Redirect, twoFactorLogin.StatusCode);
            Assert.AreEqual(3, passwordRequests);
            var challengePath = twoFactorLogin.Headers.Location!.PathAndQuery;
            Assert.Contains("/account/two-factor", challengePath);
            var callsBeforeChallenge = cookieRequests;
            using (var challenge = await twoFactorBrowser.GetAsync(challengePath, token))
                Assert.AreEqual(HttpStatusCode.OK, challenge.StatusCode);
            Assert.IsGreaterThan(callsBeforeChallenge, cookieRequests);
            Assert.IsTrue((await users.UpdateSecurityStampAsync(user)).Succeeded);
            using var invalidChallenge = await twoFactorBrowser.GetAsync(challengePath, token);
            Assert.AreEqual(HttpStatusCode.Redirect, invalidChallenge.StatusCode);

            var locked = new Account { UserName = "remote-locked" };
            Assert.IsTrue((await users.CreateAsync(locked, "Reader-Password-123")).Succeeded);
            using var identityClient = identity.CreateClient();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await identityClient.PostAsJsonAsync("/internal/identity/password-login", new PasswordLoginRequest("remote-locked", "wrong-password"), token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
            using var lockedLogin = await identityClient.PostAsJsonAsync("/internal/identity/password-login", new PasswordLoginRequest("remote-locked", "Reader-Password-123"), token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, lockedLogin.StatusCode);
        }, TestContext.CancellationToken);

    private sealed class CountingHandler(HttpMessageHandler inner, Action<HttpRequestMessage> count) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

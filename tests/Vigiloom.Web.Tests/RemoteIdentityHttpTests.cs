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
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Diagnostics;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class RemoteIdentityHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task WebApi_UsesIdentityHostForCredentialAndRevocation() =>
        ReusableWebDatabase.ExecuteAsync(async (database, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (identityDatabase, token) =>
        {
            var sessionRequests = 0;
            var cookieRequests = 0;
            var revocationRequests = 0;
            var passwordRequests = 0;
            var twoFactorCompletions = 0;
            var credentialManagementRequests = 0;
            var accountManagementRequests = 0;
            var passwordOperationRequests = 0;
            var twoFactorSettingRequests = 0;
            var auditRequests = 0;
            using var identity = new WebApplicationFactory<IdentityHost::Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:VigiloomIdentity"] = identityDatabase.ConnectionString,
                    ["Vigiloom:SetupToken"] = "test-only-bootstrap-secret-with-adequate-length"
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
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), request =>
                    {
                        if (request.RequestUri?.AbsolutePath == "/internal/identity/password-login") Interlocked.Increment(ref passwordRequests);
                        if (request.RequestUri?.AbsolutePath == "/internal/identity/two-factor/complete") Interlocked.Increment(ref twoFactorCompletions);
                    })));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteCredentialManagement))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref credentialManagementRequests))));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteAccountManagement))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref accountManagementRequests))));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteAccountPasswordOperations))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref passwordOperationRequests))));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteTwoFactorSettings))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref twoFactorSettingRequests))));
                builder.ConfigureTestServices(services => services.AddHttpClient(nameof(RemoteIdentityAuditReader))
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), _ => Interlocked.Increment(ref auditRequests))));
            });
            using (var registrationScope = web.Services.CreateScope())
                Assert.IsInstanceOfType<RemoteApiCredentialValidation>(registrationScope.ServiceProvider.GetRequiredService<IApiCredentialValidation>());
            var contexts = identity.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var identityDb = await contexts.CreateDbContextAsync(token)) await identityDb.Database.MigrateAsync(token);
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
            await using var identityScope = identity.Services.CreateAsyncScope();
            var users = identityScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
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
            await using (var verifyFailure = identity.Services.CreateAsyncScope())
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
            await identityScope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>().Entry(user).ReloadAsync(token);
            var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(options.ClaimsIdentity.UserIdClaimType, user.Id.ToString("D")),
                new Claim(options.ClaimsIdentity.SecurityStampClaimType, (await users.GetSecurityStampAsync(user))!),
                new Claim(SessionLifetime.ExpirationClaim, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
            ], "test"));
            var management = scope.ServiceProvider.GetRequiredService<CredentialManagement>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await management.ListAsync(principal, 1, token));
            var administrator = new Account { UserName = "remote-admin" };
            Assert.IsTrue((await users.CreateAsync(administrator, "Admin-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(administrator, BuiltInRoles.Administrator)).Succeeded);
            var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(options.ClaimsIdentity.UserIdClaimType, administrator.Id.ToString("D")),
                new Claim(options.ClaimsIdentity.SecurityStampClaimType, (await users.GetSecurityStampAsync(administrator))!),
                new Claim(SessionLifetime.ExpirationClaim, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
            ], "test"));
            var accountManagement = scope.ServiceProvider.GetRequiredService<AccountManagement>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await accountManagement.ListAsync(principal, 1, token));
            var createdAccount = await accountManagement.CreateAsync(adminPrincipal, "managed-reader", BuiltInRoles.ReadOnly, token);
            Assert.AreEqual(AccountOperationStatus.Success, createdAccount.Result.Status);
            Assert.IsNotNull(createdAccount.AccountId);
            var managedId = createdAccount.AccountId.Value;
            Assert.AreEqual(AccountOperationStatus.Success, (await accountManagement.RenameAsync(adminPrincipal, managedId, "renamed-reader", token)).Status);
            Assert.AreEqual("renamed-reader", (await accountManagement.GetAsync(adminPrincipal, managedId, token))!.UserName);
            Assert.AreEqual(AccountOperationStatus.Success, (await accountManagement.SetRoleAsync(adminPrincipal, managedId, BuiltInRoles.Administrator, token)).Status);
            Assert.AreEqual(AccountOperationStatus.Success, (await accountManagement.SetRoleAsync(adminPrincipal, managedId, BuiltInRoles.ReadOnly, token)).Status);
            Assert.AreEqual(AccountOperationStatus.InvalidCredentials, (await accountManagement.ResetTwoFactorAsync(adminPrincipal, managedId, "wrong-password", token)).Status);
            Assert.AreEqual(AccountOperationStatus.Success, (await accountManagement.ResetTwoFactorAsync(adminPrincipal, managedId, "Admin-Password-123", token)).Status);
            var passwordOperations = scope.ServiceProvider.GetRequiredService<AccountPasswordOperations>();
            Assert.AreEqual(InitialAdministratorStatus.InvalidSetupToken, (await passwordOperations.CreateAsync("bad", "another-admin", "Admin-Password-123", token)).Status);
            Assert.AreEqual(InitialAdministratorStatus.AlreadyInitialized, (await passwordOperations.CreateAsync("test-only-bootstrap-secret-with-adequate-length", "another-admin", "Admin-Password-123", token)).Status);
            var reset = await accountManagement.IssuePasswordResetAsync(adminPrincipal, managedId, token);
            Assert.AreEqual(AccountOperationStatus.Success, reset.Result.Status);
            Assert.IsFalse(await passwordOperations.RedeemAsync("renamed-reader", "bad", "Initial-Password-123", token));
            Assert.IsTrue(await passwordOperations.RedeemAsync("renamed-reader", reset.Code!, "Initial-Password-123", token));
            var managedUser = await users.FindByIdAsync(managedId.ToString("D"));
            var managedPrincipal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(options.ClaimsIdentity.UserIdClaimType, managedId.ToString("D")),
                new Claim(options.ClaimsIdentity.SecurityStampClaimType, (await users.GetSecurityStampAsync(managedUser!))!)
            ], "test"));
            Assert.AreEqual(AccountOperationStatus.Success, (await passwordOperations.ChangeAsync(managedPrincipal, "Initial-Password-123", "Changed-Password-456", token)).Status);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await passwordOperations.ChangeAsync(managedPrincipal, "Changed-Password-456", "Another-Password-789", token));
            Assert.AreEqual(6, passwordOperationRequests);
            Assert.AreEqual(AccountOperationStatus.Success, (await accountManagement.DeleteAsync(adminPrincipal, managedId, token)).Status);
            Assert.IsNull(await accountManagement.GetAsync(adminPrincipal, managedId, token));
            var issued = await management.IssueAsync(adminPrincipal, "remote-managed", false, 30, "Admin-Password-123", token);
            Assert.AreEqual(ApiCredentialStatus.Success, issued.Status);
            Assert.IsFalse(string.IsNullOrEmpty(issued.Secret));
            var issuedId = Guid.Parse(issued.Secret!.Substring(4, 36));
            var listed = await management.ListAsync(adminPrincipal, 1, token);
            Assert.IsTrue(listed.Credentials.Any(item => item.Id == issuedId));
            Assert.AreEqual(ApiCredentialStatus.Success, await management.RevokeAsync(adminPrincipal, issuedId, "Admin-Password-123", token));
            Assert.AreEqual(4, credentialManagementRequests);
            using var adminBrowser = web.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using var adminLoginPage = await adminBrowser.GetAsync("/account/login", token);
            var adminLoginDocument = await new HtmlParser().ParseDocumentAsync(await adminLoginPage.Content.ReadAsStringAsync(token), token);
            var adminLoginForm = adminLoginDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
            var adminLoginFields = adminLoginForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            adminLoginFields["Input.UserName"] = "remote-admin";
            adminLoginFields["Input.Password"] = "Admin-Password-123";
            using (var adminLogin = await adminBrowser.PostAsync("/account/login", new FormUrlEncodedContent(adminLoginFields), token))
                Assert.AreEqual(HttpStatusCode.Redirect, adminLogin.StatusCode);
            using var accountsPage = await adminBrowser.GetAsync("/account/manage", token);
            Assert.AreEqual(HttpStatusCode.OK, accountsPage.StatusCode);
            var accountsDocument = await new HtmlParser().ParseDocumentAsync(await accountsPage.Content.ReadAsStringAsync(token), token);
            var createForm = accountsDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "create-account");
            var createFields = createForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            createFields["Input.UserName"] = "remote-ui-reader";
            createFields["Input.Role"] = BuiltInRoles.ReadOnly;
            var beforeCreate = accountManagementRequests;
            using (var createResponse = await adminBrowser.PostAsync("/account/manage", new FormUrlEncodedContent(createFields), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, createResponse.StatusCode);
                var createdDocument = await new HtmlParser().ParseDocumentAsync(await createResponse.Content.ReadAsStringAsync(token), token);
                Assert.IsNotNull(createdDocument.QuerySelector("textarea[aria-label='一次性設定密碼代碼']"));
            }
            Assert.IsGreaterThan(beforeCreate, accountManagementRequests);
            var auditReader = scope.ServiceProvider.GetRequiredService<IdentityAuditReader>();
            var auditQuery = new OperationAuditQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1), 1);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await auditReader.QueryAsync(principal, AuditFeedSource.Accounts, auditQuery, token));
            var auditPage = await auditReader.QueryAsync(adminPrincipal, AuditFeedSource.Accounts, auditQuery, token);
            Assert.IsTrue(auditPage.Items.Any(item => item.Action == AuditFeedAction.AccountCreated));
            var actorNames = await auditReader.NamesAsync(adminPrincipal, [administrator.Id], token);
            Assert.AreEqual("remote-admin", actorNames[administrator.Id]);
            var from = Uri.EscapeDataString(auditQuery.From.ToString("O", CultureInfo.InvariantCulture));
            var to = Uri.EscapeDataString(auditQuery.To.ToString("O", CultureInfo.InvariantCulture));
            using (var auditResponse = await adminBrowser.GetAsync($"/api/v1/operations/audit-feed/accounts?from={from}&to={to}", token))
                Assert.AreEqual(HttpStatusCode.OK, auditResponse.StatusCode);
            Assert.IsGreaterThan(3, auditRequests);
            var beforeAuditPage = auditRequests;
            var fromDate = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var toDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using (var auditUi = await adminBrowser.GetAsync($"/settings/audit?source=accounts&from={fromDate}&to={toDate}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, auditUi.StatusCode);
                Assert.Contains("remote-admin", await auditUi.Content.ReadAsStringAsync(token));
            }
            Assert.IsGreaterThan(beforeAuditPage, auditRequests);
            using var managementPage = await adminBrowser.GetAsync("/account/api-credentials", token);
            Assert.AreEqual(HttpStatusCode.OK, managementPage.StatusCode);
            var managementDocument = await new HtmlParser().ParseDocumentAsync(await managementPage.Content.ReadAsStringAsync(token), token);
            var issueForm = managementDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "issue-api-credential");
            var issueFields = issueForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            issueFields["IssueInput.Name"] = "remote-from-ui";
            issueFields["IssueInput.Days"] = "30";
            issueFields["IssueInput.Password"] = "Admin-Password-123";
            using (var issueResponse = await adminBrowser.PostAsync("/account/api-credentials", new FormUrlEncodedContent(issueFields), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, issueResponse.StatusCode);
                var issueDocument = await new HtmlParser().ParseDocumentAsync(await issueResponse.Content.ReadAsStringAsync(token), token);
                Assert.IsNotNull(issueDocument.QuerySelector("textarea[aria-label='一次性 API 憑證']"));
            }
            Assert.IsGreaterThan(4, credentialManagementRequests);
            using var settingsPage = await adminBrowser.GetAsync("/account/two-factor-settings", token);
            Assert.AreEqual(HttpStatusCode.OK, settingsPage.StatusCode);
            var settingsDocument = await new HtmlParser().ParseDocumentAsync(await settingsPage.Content.ReadAsStringAsync(token), token);
            var beginForm = settingsDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "begin-two-factor");
            var beginFields = beginForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            beginFields["BeginInput.Password"] = "Admin-Password-123";
            using var beginResponse = await adminBrowser.PostAsync("/account/two-factor-settings", new FormUrlEncodedContent(beginFields), token);
            Assert.AreEqual(HttpStatusCode.OK, beginResponse.StatusCode);
            var beginDocument = await new HtmlParser().ParseDocumentAsync(await beginResponse.Content.ReadAsStringAsync(token), token);
            var settingsKey = beginDocument.QuerySelector(".account-qr code")?.TextContent;
            Assert.IsFalse(string.IsNullOrEmpty(settingsKey));
            var enableForm = beginDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "enable-two-factor");
            var enableFields = enableForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            enableFields["ConfirmInput.Code"] = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(settingsKey!)).ComputeTotp();
            using var enableResponse = await adminBrowser.PostAsync("/account/two-factor-settings", new FormUrlEncodedContent(enableFields), token);
            Assert.AreEqual(HttpStatusCode.OK, enableResponse.StatusCode);
            var enabledDocument = await new HtmlParser().ParseDocumentAsync(await enableResponse.Content.ReadAsStringAsync(token), token);
            Assert.IsNotNull(enabledDocument.QuerySelector("textarea[aria-label='雙重驗證復原碼']"));
            var disableForm = enabledDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "disable-two-factor");
            var disableFields = disableForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            disableFields["DisableInput.Password"] = "Admin-Password-123";
            disableFields["DisableInput.Code"] = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(settingsKey!)).ComputeTotp();
            using var disableResponse = await adminBrowser.PostAsync("/account/two-factor-settings", new FormUrlEncodedContent(disableFields), token);
            Assert.AreEqual(HttpStatusCode.OK, disableResponse.StatusCode);
            var disabledDocument = await new HtmlParser().ParseDocumentAsync(await disableResponse.Content.ReadAsStringAsync(token), token);
            Assert.Contains("雙重驗證已停用", disabledDocument.QuerySelector(".account-success")?.TextContent ?? "");
            Assert.IsGreaterThan(3, twoFactorSettingRequests);
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
            await identityScope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>().Entry(user).ReloadAsync(token);
            using (var expiredCookie = await browser.GetAsync("/api/v1/workspace/identity", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, expiredCookie.StatusCode);
            Assert.IsNull(await workspace.GetAsync(principal, token));
            Assert.IsFalse(await revalidation.RevalidateAsync(principal, token));
            Assert.AreEqual(7, sessionRequests);

            Assert.IsTrue((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            var authenticatorKey = await users.GetAuthenticatorKeyAsync(user);
            Assert.IsFalse(string.IsNullOrEmpty(authenticatorKey));
            await using (var keyScope = identity.Services.CreateAsyncScope())
            {
                var identityUsers = keyScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
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
            Assert.AreEqual(4, passwordRequests);
            var challengePath = twoFactorLogin.Headers.Location!.PathAndQuery;
            Assert.Contains("/account/two-factor", challengePath);
            var callsBeforeChallenge = cookieRequests;
            using var challenge = await twoFactorBrowser.GetAsync(challengePath, token);
            {
                Assert.AreEqual(HttpStatusCode.OK, challenge.StatusCode);
                var challengeDocument = await new HtmlParser().ParseDocumentAsync(await challenge.Content.ReadAsStringAsync(token), token);
                var challengeForm = challengeDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "totp-login");
                var challengeFields = challengeForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
                challengeFields["TotpInput.Code"] = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(authenticatorKey!)).ComputeTotp();
                using var completed = await twoFactorBrowser.PostAsync(challengePath, new FormUrlEncodedContent(challengeFields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, completed.StatusCode);
            }
            Assert.AreEqual(1, twoFactorCompletions);
            using (var authenticated = await twoFactorBrowser.GetAsync("/api/v1/workspace/identity", token))
                Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
            Assert.IsGreaterThan(callsBeforeChallenge, cookieRequests);
            using var pendingBrowser = web.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using var pendingPage = await pendingBrowser.GetAsync("/account/login", token);
            var pendingDocument = await new HtmlParser().ParseDocumentAsync(await pendingPage.Content.ReadAsStringAsync(token), token);
            var pendingForm = pendingDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
            var pendingFields = pendingForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            pendingFields["Input.UserName"] = "remote-reader";
            pendingFields["Input.Password"] = "Reader-Password-123";
            using var pendingLogin = await pendingBrowser.PostAsync("/account/login", new FormUrlEncodedContent(pendingFields), token);
            Assert.AreEqual(HttpStatusCode.Redirect, pendingLogin.StatusCode);
            await identityScope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>().Entry(user).ReloadAsync(token);
            Assert.IsTrue((await users.UpdateSecurityStampAsync(user)).Succeeded);
            using var invalidChallenge = await pendingBrowser.GetAsync(challengePath, token);
            Assert.AreEqual(HttpStatusCode.Redirect, invalidChallenge.StatusCode);

            var recoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.ToArray();
            var recoveryStamp = await users.GetSecurityStampAsync(user);
            using var identityClient = identity.CreateClient();
            var recoveryRequest = new CompleteTwoFactorRequest(user.Id, recoveryStamp, recoveryCodes[0], true);
            using (var invalidRecovery = await identityClient.PostAsJsonAsync("/internal/identity/two-factor/complete", recoveryRequest with { Code = "invalid" }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, invalidRecovery.StatusCode);
            using (var validRecovery = await identityClient.PostAsJsonAsync("/internal/identity/two-factor/complete", recoveryRequest, token))
                Assert.AreEqual(HttpStatusCode.OK, validRecovery.StatusCode);
            using (var replayedRecovery = await identityClient.PostAsJsonAsync("/internal/identity/two-factor/complete", recoveryRequest, token))
                Assert.AreEqual(HttpStatusCode.Conflict, replayedRecovery.StatusCode);
            await using (var verifyRecovery = identity.Services.CreateAsyncScope())
            {
                var identityUsers = verifyRecovery.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var identityUser = await identityUsers.FindByIdAsync(user.Id.ToString("D"));
                Assert.AreEqual(1, await identityUsers.GetAccessFailedCountAsync(identityUser!));
            }

            var locked = new Account { UserName = "remote-locked" };
            Assert.IsTrue((await users.CreateAsync(locked, "Reader-Password-123")).Succeeded);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await identityClient.PostAsJsonAsync("/internal/identity/password-login", new PasswordLoginRequest("remote-locked", "wrong-password"), token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
            using var lockedLogin = await identityClient.PostAsJsonAsync("/internal/identity/password-login", new PasswordLoginRequest("remote-locked", "Reader-Password-123"), token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, lockedLogin.StatusCode);
        }, outerToken), TestContext.CancellationToken);

    private sealed class CountingHandler(HttpMessageHandler inner, Action<HttpRequestMessage> count) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

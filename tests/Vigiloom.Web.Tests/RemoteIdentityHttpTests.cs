extern alias IdentityHost;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Application;
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
                    .ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(identity.Server.CreateHandler(), () => Interlocked.Increment(ref sessionRequests))));
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
            Assert.IsTrue((await users.CreateAsync(user)).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(user, BuiltInRoles.ReadOnly)).Succeeded);
            var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(options.ClaimsIdentity.UserIdClaimType, user.Id.ToString("D")),
                new Claim(options.ClaimsIdentity.SecurityStampClaimType, (await users.GetSecurityStampAsync(user))!),
                new Claim(SessionLifetime.ExpirationClaim, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
            ], "test"));
            var workspace = scope.ServiceProvider.GetRequiredService<WorkspaceIdentityService>();
            var validSession = await workspace.GetAsync(principal, token);
            Assert.AreEqual("remote-reader", validSession!.UserName);
            Assert.Contains(BuiltInRoles.ReadOnly, validSession.Roles);
            Assert.IsTrue((await users.UpdateSecurityStampAsync(user)).Succeeded);
            Assert.IsNull(await workspace.GetAsync(principal, token));
            Assert.AreEqual(2, sessionRequests);
        }, TestContext.CancellationToken);

    private sealed class CountingHandler(HttpMessageHandler inner, Action count) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            return base.SendAsync(request, cancellationToken);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Identity.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SessionValidationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Validate_UsesCurrentAccountRolesAndSecurityStamp() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:VigiloomIdentity"] = database.ConnectionString })));
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
            await db.Database.MigrateAsync(token);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<Role>>();
            Assert.IsTrue((await roles.CreateAsync(new Role("ReadOnly"))).Succeeded);
            var account = new Account { UserName = "reader" };
            Assert.IsTrue((await users.CreateAsync(account)).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(account, "ReadOnly")).Succeeded);
            var stamp = await users.GetSecurityStampAsync(account);
            var request = new ValidateSessionRequest(account.Id, stamp);
            using var client = factory.CreateClient();
            using (var response = await client.PostAsJsonAsync("/internal/identity/sessions/validate", request, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore ?? false);
                var session = await response.Content.ReadFromJsonAsync<ValidatedSessionResponse>(cancellationToken: token);
                Assert.AreEqual(account.Id, session!.AccountId);
                CollectionAssert.Contains(session.Roles, "ReadOnly");
            }
            Assert.IsTrue((await users.UpdateSecurityStampAsync(account)).Succeeded);
            using var revoked = await client.PostAsJsonAsync("/internal/identity/sessions/validate", request, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }, TestContext.CancellationToken);
}

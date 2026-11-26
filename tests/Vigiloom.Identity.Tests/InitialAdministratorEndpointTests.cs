using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Identity.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InitialAdministratorEndpointTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Initialize_CreatesOneAdministrator() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string setupToken = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:VigiloomIdentity"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = setupToken
                })));
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            using var client = factory.CreateClient();
            using (var invalid = await client.PostAsJsonAsync("/internal/identity/accounts/initialize", new InitialAdministratorRequest(setupToken, "admin", "short"), token))
            {
                var result = await invalid.Content.ReadFromJsonAsync<InitialAdministratorResult>(cancellationToken: token);
                Assert.IsNotNull(result?.ErrorCodes);
                Assert.Contains("PasswordTooShort", result.ErrorCodes);
                Assert.HasCount(result.Errors.Count, result.ErrorCodes);
            }
            var request = new InitialAdministratorRequest(setupToken, "admin", "Admin-Password-123");
            using (var first = await client.PostAsJsonAsync("/internal/identity/accounts/initialize", request, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                Assert.IsTrue(first.Headers.CacheControl?.NoStore ?? false);
                Assert.AreEqual(InitialAdministratorStatus.Created, (await first.Content.ReadFromJsonAsync<InitialAdministratorResult>(cancellationToken: token))!.Status);
            }
            using var second = await client.PostAsJsonAsync("/internal/identity/accounts/initialize", request, token);
            Assert.AreEqual(InitialAdministratorStatus.AlreadyInitialized, (await second.Content.ReadFromJsonAsync<InitialAdministratorResult>(cancellationToken: token))!.Status);
        }, TestContext.CancellationToken);
}

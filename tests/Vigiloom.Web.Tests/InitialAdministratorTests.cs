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

using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Recovery;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AccountAdministrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AccountOperations_RequireAuthorizationAndProtectLastAdministratorInParallel() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string secret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = secret
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setup = scope.ServiceProvider.GetRequiredService<InitialAdministrator>();
                Assert.AreEqual(InitialAdministratorStatus.Created,
                    (await setup.CreateAsync(secret, "alice", "Strong-Password-123", token)).Status);
            }
            async Task<(Guid Id, ClaimsPrincipal Principal)> Actor(string name)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await manager.FindByNameAsync(name);
                Assert.IsNotNull(account);
                var claims = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<Account>>();
                return (account.Id, await claims.CreateAsync(account));
            }
            async Task<ResetCodeResult> Create(ClaimsPrincipal principal, string name, string role)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<AccountAdministration>()
                    .CreateAsync(principal, name, role, token);
            }
            var alice = await Actor("alice");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var administration = scope.ServiceProvider.GetRequiredService<AccountAdministration>();
                Assert.AreEqual(AccountOperationStatus.SelfDeletion, (await administration.DeleteAsync(alice.Principal, alice.Id, token)).Status);
                Assert.AreEqual(AccountOperationStatus.LastAdministrator,
                    (await administration.SetRoleAsync(alice.Principal, alice.Id, BuiltInRoles.ReadOnly, token)).Status);
                Assert.AreEqual(AccountOperationStatus.LastAdministrator,
                    (await administration.IssuePasswordResetAsync(alice.Principal, alice.Id, token)).Result.Status);
            }
            var readerCreation = await Create(alice.Principal, "reader", BuiltInRoles.ReadOnly);
            Assert.AreEqual(AccountOperationStatus.Success, readerCreation.Result.Status);
            Assert.IsNotNull(readerCreation.Code);
            async Task<bool> Redeem(string name, string code)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<AccountPasswordReset>()
                    .RedeemAsync(name, code, "Strong-Password-123", token);
            }
            Assert.IsFalse(await Redeem("reader", "invalid"));
            Assert.IsTrue(await Redeem("reader", readerCreation.Code));
            Assert.IsFalse(await Redeem("reader", readerCreation.Code));
            var reader = await Actor("reader");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var admin = scope.ServiceProvider.GetRequiredService<AccountAdministration>();
                var reset = await admin.IssuePasswordResetAsync(alice.Principal, reader.Id, token);
                Assert.AreEqual(AccountOperationStatus.Success, reset.Result.Status);
                Assert.IsNotNull(reset.Code);
                Assert.IsFalse(await Redeem("reader", readerCreation.Code));
                Assert.IsTrue(await Redeem("reader", reset.Code));
            }
            var bobCreation = await Create(alice.Principal, "bob", BuiltInRoles.Administrator);
            Assert.AreEqual(AccountOperationStatus.Success, bobCreation.Result.Status);
            Assert.IsNotNull(bobCreation.Code);
            Assert.IsTrue(await Redeem("bob", bobCreation.Code));
            reader = await Actor("reader");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var change = scope.ServiceProvider.GetRequiredService<SelfPasswordChange>();
                Assert.AreEqual(AccountOperationStatus.ValidationFailed,
                    (await change.ChangeAsync(reader.Principal, "wrong", "Different-Password-456", token)).Status);
                Assert.AreEqual(AccountOperationStatus.Success,
                    (await change.ChangeAsync(reader.Principal, "Strong-Password-123", "Different-Password-456", token)).Status);
            }
            await using (var scope = factory.Services.CreateAsyncScope())
                await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await scope.ServiceProvider.GetRequiredService<SelfPasswordChange>()
                    .ChangeAsync(reader.Principal, "Different-Password-456", "Third-Password-789", token));
            reader = await Actor("reader");
            var bob = await Actor("bob");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var administration = scope.ServiceProvider.GetRequiredService<AccountAdministration>();
                await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await administration.ListAsync(reader.Principal, cancellationToken: token));
                Assert.AreEqual(3, (await administration.ListAsync(alice.Principal, cancellationToken: token)).TotalCount);
            }
            async Task<AccountOperationStatus?> Demote(ClaimsPrincipal principal, Guid target)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                try
                {
                    return (await scope.ServiceProvider.GetRequiredService<AccountAdministration>()
                        .SetRoleAsync(principal, target, BuiltInRoles.ReadOnly, token)).Status;
                }
                catch (UnauthorizedAccessException) { return null; }
            }
            var race = await Task.WhenAll(Demote(alice.Principal, bob.Id), Demote(bob.Principal, alice.Id));
            Assert.HasCount(1, race.Where(status => status == AccountOperationStatus.Success));
            Assert.HasCount(1, race.Where(status => status is null or AccountOperationStatus.LastAdministrator));
            await using var check = await contexts.CreateDbContextAsync(token);
            var adminId = (await check.Roles.SingleAsync(role => role.NormalizedName == "ADMINISTRATOR", token)).Id;
            Assert.AreEqual(1, await check.UserRoles.CountAsync(role => role.RoleId == adminId, token));
            Assert.AreEqual(9, await check.AccountAuditEvents.CountAsync(token));
            var remainingId = (await check.UserRoles.SingleAsync(role => role.RoleId == adminId, token)).UserId;
            string remainingName;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var remaining = await manager.FindByIdAsync(remainingId.ToString());
                Assert.IsNotNull(remaining);
                remainingName = remaining.UserName!;
                Assert.IsTrue((await manager.SetTwoFactorEnabledAsync(remaining, true)).Succeeded);
                Assert.IsTrue((await manager.SetLockoutEndDateAsync(remaining, DateTimeOffset.UtcNow.AddHours(1))).Succeeded);
            }
            // Use a separate service provider like the local command, not the Web host's provider.
            var recoveryServices = new ServiceCollection();
            recoveryServices.AddLogging();
            recoveryServices.AddVigiloomDataProtection(new ConfigurationBuilder().Build());
            recoveryServices.AddModuleSqlServer<IdentityAccessDbContext>(database.ConnectionString, "identity");
            recoveryServices.AddIdentityCore<Account>().AddRoles<Role>().AddEntityFrameworkStores<IdentityAccessDbContext>().AddDefaultTokenProviders();
            recoveryServices.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(15));
            string recoveryCode;
            using (var recoveryProvider = recoveryServices.BuildServiceProvider())
            await using (var scope = recoveryProvider.CreateAsyncScope())
            {
                var recovery = new AdministratorRecovery(scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>(),
                    scope.ServiceProvider.GetRequiredService<UserManager<Account>>());
                Assert.IsNull(await recovery.IssueCodeAsync("reader", token));
                var issued = await recovery.IssueCodeAsync(remainingName, token);
                Assert.IsNotNull(issued);
                recoveryCode = issued;
            }
            Assert.IsTrue(await Redeem(remainingName, recoveryCode));
            Assert.IsFalse(await Redeem(remainingName, recoveryCode));
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var recovered = await manager.FindByIdAsync(remainingId.ToString());
                Assert.IsNotNull(recovered);
                Assert.IsFalse(await manager.IsLockedOutAsync(recovered));
                Assert.IsFalse(await manager.GetTwoFactorEnabledAsync(recovered));
                Assert.IsTrue(await manager.CheckPasswordAsync(recovered, "Strong-Password-123"));
            }
            Assert.AreEqual(11, await check.AccountAuditEvents.CountAsync(token));
        }, TestContext.CancellationToken);
}

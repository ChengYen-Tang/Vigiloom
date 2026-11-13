using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TwoFactorStoreTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task UserCanEnableAndDisableAuthenticatorWithRecoveryCode() =>
        Fixtures.ReusableWebDatabase.ExecuteAsync(async (database, token) =>
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
            await RoleInitializer.InitializeAsync(contexts, token);
            await using (var scope = factory.Services.CreateAsyncScope())
                Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                    .CreateAsync(secret, "admin", "Strong-Password-123", token)).Status);
            async Task<System.Security.Claims.ClaimsPrincipal> Principal()
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await manager.FindByNameAsync("admin");
                Assert.IsNotNull(account);
                return await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<Account>>().CreateAsync(account);
            }
            var principal = await Principal();
            string key;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var management = scope.ServiceProvider.GetRequiredService<TwoFactorManagement>();
                Assert.AreEqual(TwoFactorStatus.InvalidCredentials, (await management.BeginAsync(principal, "wrong", token)).Status);
                var started = await management.BeginAsync(principal, "Strong-Password-123", token);
                Assert.AreEqual(TwoFactorStatus.Success, started.Status);
                Assert.IsNotNull(started.Key);
                key = started.Key;
            }
            principal = await Principal();
            string recoveryCode;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var management = scope.ServiceProvider.GetRequiredService<TwoFactorManagement>();
                Assert.AreEqual(TwoFactorStatus.InvalidCredentials, (await management.EnableAsync(principal, "000000", token)).Status);
                var enabled = await management.EnableAsync(principal, new Totp(Base32Encoding.ToBytes(key)).ComputeTotp(), token);
                Assert.AreEqual(TwoFactorStatus.Success, enabled.Status);
                Assert.HasCount(10, enabled.RecoveryCodes);
                recoveryCode = enabled.RecoveryCodes[0];
            }
            principal = await Principal();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var management = scope.ServiceProvider.GetRequiredService<TwoFactorManagement>();
                Assert.AreEqual(TwoFactorStatus.InvalidCredentials, (await management.DisableAsync(principal, "wrong", recoveryCode, token)).Status);
                Assert.AreEqual(TwoFactorStatus.Success, (await management.DisableAsync(principal, "Strong-Password-123", recoveryCode, token)).Status);
            }
            await using var checkScope = factory.Services.CreateAsyncScope();
            var managerCheck = checkScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var checkedAccount = await managerCheck.FindByNameAsync("admin");
            Assert.IsNotNull(checkedAccount);
            Assert.IsFalse(await managerCheck.GetTwoFactorEnabledAsync(checkedAccount));
            Assert.AreEqual(0, await managerCheck.CountRecoveryCodesAsync(checkedAccount));
            Assert.IsNull(await managerCheck.GetAuthenticatorKeyAsync(checkedAccount));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task AuthenticatorSecrets_AreEncryptedAndRecoveryCodesAreSingleUseUnderConcurrency() =>
        Fixtures.ReusableWebDatabase.ExecuteAsync(async (database, token) =>
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
            await RoleInitializer.InitializeAsync(contexts, token);
            Guid userId;
            string authenticatorKey;
            string recoveryCode;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setup = scope.ServiceProvider.GetRequiredService<InitialAdministrator>();
                Assert.AreEqual(InitialAdministratorStatus.Created, (await setup.CreateAsync(secret, "admin", "Strong-Password-123", token)).Status);
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await manager.FindByNameAsync("admin");
                Assert.IsNotNull(account);
                userId = account.Id;
                Assert.IsTrue((await manager.ResetAuthenticatorKeyAsync(account)).Succeeded);
                authenticatorKey = (await manager.GetAuthenticatorKeyAsync(account))!;
                Assert.IsTrue((await manager.SetTwoFactorEnabledAsync(account, true)).Succeeded);
                var codes = (await manager.GenerateNewTwoFactorRecoveryCodesAsync(account, 4))!.ToArray();
                Assert.HasCount(4, codes);
                recoveryCode = codes[0];
            }
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var values = await db.UserTokens.Where(item => item.UserId == userId).Select(item => item.Value).ToArrayAsync(token);
                Assert.HasCount(2, values);
                Assert.IsFalse(values.Any(value => value is not null && (value.Contains(authenticatorKey, StringComparison.Ordinal) || value.Contains(recoveryCode, StringComparison.Ordinal))));
            }
            async Task<bool> Redeem()
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = await manager.FindByIdAsync(userId.ToString());
                Assert.IsNotNull(account);
                return (await manager.RedeemTwoFactorRecoveryCodeAsync(account, recoveryCode)).Succeeded;
            }
            var results = await Task.WhenAll(Redeem(), Redeem());
            Assert.HasCount(1, results.Where(success => success));
            await using var checkScope = factory.Services.CreateAsyncScope();
            var checkManager = checkScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var checkedAccount = await checkManager.FindByIdAsync(userId.ToString());
            Assert.IsNotNull(checkedAccount);
            Assert.AreEqual(3, await checkManager.CountRecoveryCodesAsync(checkedAccount));
            Assert.AreEqual(authenticatorKey, await checkManager.GetAuthenticatorKeyAsync(checkedAccount));
        }, TestContext.CancellationToken);
}

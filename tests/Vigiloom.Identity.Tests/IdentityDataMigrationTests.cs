using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Identity.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IdentityDataMigrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Copy_PreservesIdentityAndRejectsNonEmptyTarget() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            using var sourceHost = Host(source.ConnectionString);
            using var targetHost = Host(target.ConnectionString);
            var sourceContexts = sourceHost.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            var targetContexts = targetHost.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await sourceContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await targetContexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(sourceContexts, token);
            Guid userId;
            string stamp;
            string key;
            Guid credentialId;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var user = new Account { UserName = "migrated-user" };
                Assert.IsTrue((await users.CreateAsync(user, "Existing-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(user, BuiltInRoles.Administrator)).Succeeded);
                Assert.IsTrue((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
                Assert.IsTrue((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
                userId = user.Id;
                stamp = await users.GetSecurityStampAsync(user);
                key = (await users.GetAuthenticatorKeyAsync(user))!;
                Assert.IsFalse(string.IsNullOrEmpty(key));
                var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
                var credential = new ApiCredential("existing", SHA256.HashData(RandomNumberGenerator.GetBytes(32)), true, DateTimeOffset.UtcNow.AddDays(1), userId);
                credentialId = credential.Id;
                db.ApiCredentials.Add(credential);
                db.AccountAuditEvents.Add(new AccountAuditEvent(userId, userId, "InitialAdministratorCreated", DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(token);
            }
            await using (var connection = new SqlConnection(target.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using (var reject = new SqlCommand("ALTER TABLE [identity].[ApiCredentials] ADD CONSTRAINT [CK_TestRejectWrite] CHECK ([CanWrite] = 0)", connection))
                    await reject.ExecuteNonQueryAsync(token);
                await Assert.ThrowsAsync<SqlException>(async () => await IdentityDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
                await using (var count = new SqlCommand("SELECT COUNT(*) FROM [identity].[AspNetUsers]", connection))
                    Assert.AreEqual(0, (int)(await count.ExecuteScalarAsync(token))!);
                await using (var remove = new SqlCommand("ALTER TABLE [identity].[ApiCredentials] DROP CONSTRAINT [CK_TestRejectWrite]", connection))
                    await remove.ExecuteNonQueryAsync(token);
            }
            await IdentityDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using (var scope = targetHost.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var user = await users.FindByIdAsync(userId.ToString("D"));
                Assert.IsNotNull(user);
                Assert.AreEqual(stamp, await users.GetSecurityStampAsync(user));
                Assert.AreEqual(key, await users.GetAuthenticatorKeyAsync(user));
                Assert.IsTrue(await users.CheckPasswordAsync(user, "Existing-Password-123"));
                Assert.IsTrue(await users.IsInRoleAsync(user, BuiltInRoles.Administrator));
                var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
                Assert.IsTrue(await db.ApiCredentials.AnyAsync(item => item.Id == credentialId, token));
                Assert.IsTrue(await db.AccountAuditEvents.AnyAsync(item => item.Action == "InitialAdministratorCreated", token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await IdentityDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
        }, outerToken), TestContext.CancellationToken);

    private static WebApplicationFactory<Program> Host(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:VigiloomIdentity"] = connection
            })));
}

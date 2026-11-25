using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Identity.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IdentityDeploymentTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Migrator_InitializesAndCopiesExistingIdentity() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            Assert.AreEqual(0, await RunAsync("initialize-identity", null, source.ConnectionString, token));
            using var sourceHost = Host(source.ConnectionString);
            Guid accountId;
            await using (var scope = sourceHost.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var account = new Account { UserName = "deployment-user" };
                Assert.IsTrue((await users.CreateAsync(account, "Existing-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(account, BuiltInRoles.Administrator)).Succeeded);
                accountId = account.Id;
            }
            Assert.AreEqual(0, await RunAsync("migrate-identity", source.ConnectionString, target.ConnectionString, token));
            using var targetHost = Host(target.ConnectionString);
            await using var targetScope = targetHost.Services.CreateAsyncScope();
            var targetUsers = targetScope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var copied = await targetUsers.FindByIdAsync(accountId.ToString("D"));
            Assert.IsNotNull(copied);
            Assert.IsTrue(await targetUsers.CheckPasswordAsync(copied, "Existing-Password-123"));
            Assert.IsTrue(await targetUsers.IsInRoleAsync(copied, BuiltInRoles.Administrator));
            Assert.AreNotEqual(0, await RunAsync("migrate-identity", source.ConnectionString, target.ConnectionString, token));
        }, outerToken), TestContext.CancellationToken);

    private static WebApplicationFactory<Program> Host(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:VigiloomIdentity"] = connection
            })));

    private static async Task<int> RunAsync(string mode, string? source, string target, CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Vigiloom.Migrator.dll"));
        start.ArgumentList.Add(mode);
        start.Environment["VIGILOOM_IDENTITY_SQL_CONNECTION"] = target;
        if (source is null) start.Environment.Remove("VIGILOOM_MIGRATION_SQL_CONNECTION");
        else start.Environment["VIGILOOM_MIGRATION_SQL_CONNECTION"] = source;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Migrator.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}

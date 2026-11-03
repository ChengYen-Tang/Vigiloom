using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Authentication;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Web.Tests.Fixtures;

internal static class PingTestSite
{
    internal static async Task<Device> InitializeAsync(WebApplicationFactory<Program> factory, CancellationToken token)
    {
        var identity = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        await using (var db = await identity.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        var network = factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        await using (var db = await network.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await RoleInitializer.InitializeAsync(identity, token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            foreach (var (name, role) in new[] { ("admin", BuiltInRoles.Administrator), ("reader", BuiltInRoles.ReadOnly) })
            {
                var user = new Account { UserName = name };
                Assert.IsTrue((await users.CreateAsync(user, "Strong-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(user, role)).Succeeded);
            }
        }
        Device device;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            device = new Device("Loopback router", "127.0.0.1", false, DateTimeOffset.UtcNow);
            db.Devices.Add(device); await db.SaveChangesAsync(token);
        }
        return device;
    }

    internal static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = connectionString,
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
            }));
        });

}

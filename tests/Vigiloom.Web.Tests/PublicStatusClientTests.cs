using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class PublicStatusClientTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ServerFacade_UsesPublishedProjectionAndBoundsConcurrentReads() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        using var scope = host.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IPublicStatusClient>();
        Assert.IsNull(await client.DeviceAsync(device.Id, token));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            (await db.Devices.SingleAsync(x => x.Id == device.Id, token)).SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        var published = await client.DeviceAsync(device.Id, token);
        Assert.IsNotNull(published);
        Assert.AreEqual("Public network", published.Name);
        Assert.DoesNotContain(device.Address, System.Text.Json.JsonSerializer.Serialize(published));
        Assert.IsNull(await client.DeviceHistoryAsync(device.Id, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, token));

        var admission = host.Services.GetRequiredService<PublicStatusAdmission>();
        for (var i = 0; i < 32; i++) await admission.Gate.WaitAsync(token);
        try { await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.DeviceAsync(device.Id, token)); }
        finally { admission.Gate.Release(32); }
        Assert.IsNotNull(await client.DeviceAsync(device.Id, token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.DevicesAsync(null, 0, null, token));
    }, context.CancellationToken);
}

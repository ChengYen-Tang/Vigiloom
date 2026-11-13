using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplatePublicProjectionTests(TestContext context)
{
    [TestMethod]
    public Task Template_RestrictsPublicFieldsAndRetirementRemovesGroupMember() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var host = PingTestSite.CreateFactory(database.ConnectionString);
            var now = DateTimeOffset.UtcNow;
            var template = new PublicationTemplate("Policy", false, true, now);
            var device = new Device("Internal host", "127.0.0.1", false, now);
            var group = new Group("Internal group", now);
            device.SetTemplatePublication("Public device", template.Id, false, now);
            group.SetPublication(true, "Public group", PublicationPolicy.BaselineVersion, now);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            { db.Templates.Add(template); await db.SaveChangesAsync(token); }
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            { db.Devices.Add(device); db.Groups.Add(group); db.DeviceGroups.Add(new(group.Id, device.Id)); await db.SaveChangesAsync(token); }
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
            {
                db.PingTargets.Add(new PingTargetState { DeviceId = device.Id, Target = device.Address, DeviceVersion = device.Version,
                    Enabled = true, DueAt = now, State = Reachability.Reachable, MeasuredAt = now, Outcome = PingOutcome.Success, LatencyMilliseconds = 12 });
                await db.SaveChangesAsync(token);
            }
            using var client = PingTestSite.Client(host);
            var publicDevice = await client.GetFromJsonAsync<PublicDevice>($"/api/v1/public/devices/{device.Id:D}", token);
            Assert.IsNotNull(publicDevice);
            Assert.AreEqual("Operational", publicDevice.State);
            Assert.IsNull(publicDevice.LatencyMilliseconds);
            Assert.IsNotNull(publicDevice.ObservedAt);
            var list = await client.GetFromJsonAsync<PublicPage<PublicDevice>>("/api/v1/public/devices?search=Public", token);
            Assert.IsNotNull(list);
            Assert.HasCount(1, list.Items);
            Assert.IsNull(list.Items[0].LatencyMilliseconds);
            Assert.IsNotNull(list.Items[0].ObservedAt);
            var members = await client.GetFromJsonAsync<PublicPage<PublicDevice>>($"/api/v1/public/groups/{group.Id:D}/devices", token);
            Assert.IsNotNull(members);
            Assert.HasCount(1, members.Items);
            Assert.IsNull(members.Items[0].LatencyMilliseconds);
            Assert.IsNotNull(members.Items[0].ObservedAt);
            var publishedGroup = await client.GetFromJsonAsync<PublicGroup>($"/api/v1/public/groups/{group.Id:D}", token);
            Assert.IsNotNull(publishedGroup);
            Assert.AreEqual(1, publishedGroup.DeviceCount);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            {
                var row = await db.Templates.SingleAsync(x => x.Id == template.Id, token);
                row.Update("Policy", true, false, now.AddSeconds(1));
                await db.SaveChangesAsync(token);
            }
            publicDevice = await client.GetFromJsonAsync<PublicDevice>($"/api/v1/public/devices/{device.Id:D}", token);
            Assert.IsNotNull(publicDevice);
            Assert.AreEqual(12, publicDevice.LatencyMilliseconds);
            Assert.IsNull(publicDevice.ObservedAt);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                foreach (var invalid in new[] { "TEMPLATE-V1", "template-v1 " })
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicPolicyVersion] = {invalid} WHERE [Id] = {device.Id}", token);
                    using var rejected = await client.GetAsync($"/api/v1/public/devices/{device.Id:D}", token);
                    Assert.AreEqual(HttpStatusCode.NotFound, rejected.StatusCode);
                    using var rejectedGroup = await client.GetAsync($"/api/v1/public/groups/{group.Id:D}", token);
                    Assert.AreEqual(HttpStatusCode.NotFound, rejectedGroup.StatusCode);
                }
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [resources].[Devices] SET [PublicPolicyVersion] = {PublicationPolicy.TemplateVersion} WHERE [Id] = {device.Id}", token);
            }
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
            { var row = await db.Templates.SingleAsync(x => x.Id == template.Id, token); row.Retire(now.AddSeconds(2)); await db.SaveChangesAsync(token); }
            using var after = await client.GetAsync($"/api/v1/public/devices/{device.Id:D}", token);
            Assert.AreEqual(HttpStatusCode.NotFound, after.StatusCode);
            members = await client.GetFromJsonAsync<PublicPage<PublicDevice>>($"/api/v1/public/groups/{group.Id:D}/devices", token);
            Assert.IsNotNull(members);
            Assert.IsEmpty(members.Items);
            using var hiddenGroup = await client.GetAsync($"/api/v1/public/groups/{group.Id:D}", token);
            Assert.AreEqual(HttpStatusCode.NotFound, hiddenGroup.StatusCode);
        }, context.CancellationToken);
}

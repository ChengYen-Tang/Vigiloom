using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Events;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Operations.Work;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TimelineTests(TestContext context)
{
    [TestMethod]
    public Task EventsAreDeduplicatedAndFilteredByResourceGroup() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(database.ConnectionString, "operations");
            services.AddModuleSqlServer<SchedulingDbContext>(database.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Operations"] = database.ConnectionString }).Build();
            var handler = new OperationsWorkHandler(config, new NotificationWriter(), new OperationAuditWriter(), new TimelineWriter());
            var device = Guid.CreateVersion7();
            var service = Guid.CreateVersion7();
            var groupId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var hostEntry = new EventTimelineDelivery(Guid.CreateVersion7(), new("host-log", now, device, "device", "Host event", "reference", $"/devices/{device:D}"));
            var serviceEntry = new EventTimelineDelivery(Guid.CreateVersion7(), new("service-health", now, service, "service", "Health event", "reference", $"/services/{service:D}"));
            foreach (var item in new[] { hostEntry, serviceEntry })
            {
                var delivery = new ServiceWork(EventTimelineWork.Kind, JsonSerializer.Serialize(item), Guid.CreateVersion7().ToString("D"));
                await handler.ExecuteAsync(delivery, token);
                await handler.ExecuteAsync(delivery with { DeliveryId = Guid.CreateVersion7().ToString("D") }, token);
            }
            var conflicting = hostEntry with { Entry = hostEntry.Entry with { Summary = "Changed" } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(new(EventTimelineWork.Kind,
                JsonSerializer.Serialize(conflicting), Guid.CreateVersion7().ToString("D")), token));
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = database.ConnectionString, ["Services:Resources:Url"] = "http://localhost/" }));
                web.ConfigureTestServices(s =>
                {
                    HostWorkers.RemoveBackgroundWork(s);
                    s.AddHttpClient("Resources").ConfigurePrimaryHttpMessageHandler(() => new GroupHandler(new(groupId, [device], [])));
                });
            });
            using var client = host.CreateClient();
            var from = Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"));
            var until = Uri.EscapeDataString(now.AddMinutes(1).ToString("O"));
            var all = await client.GetFromJsonAsync<EventTimelinePage>($"/internal/v1/events?from={from}&until={until}", token);
            Assert.HasCount(2, all!.Items);
            var filtered = await client.GetFromJsonAsync<EventTimelinePage>($"/internal/v1/events?from={from}&until={until}&groupId={groupId:D}", token);
            Assert.AreEqual(device, filtered!.Items.Single().ResourceId);
            var actor = Guid.CreateVersion7();
            var access = new OperationAudit(actor, actor, now, new EventTimelineAccessed(now.AddMinutes(-1), now.AddMinutes(1), groupId, null, 1, 1, false));
            using (var saved = await client.PostAsJsonAsync("/internal/v1/audit/event-access", access, token))
                Assert.AreEqual(HttpStatusCode.NoContent, saved.StatusCode);
            var audit = await client.GetFromJsonAsync<OperationAuditPage>($"/internal/v1/audit/event-timeline?from={from}&to={until}", token);
            Assert.AreEqual(actor, Assert.ContainsSingle(audit!.Items).ActorId);
            var wrongKind = hostEntry with { MessageId = Guid.CreateVersion7(), Entry = hostEntry.Entry with { ResourceKind = "service" } };
            await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(new(EventTimelineWork.Kind,
                JsonSerializer.Serialize(wrongKind), Guid.CreateVersion7().ToString("D")), token));
        }, context.CancellationToken);

    private sealed class GroupHandler(GroupMembers members) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(members) });
    }
}

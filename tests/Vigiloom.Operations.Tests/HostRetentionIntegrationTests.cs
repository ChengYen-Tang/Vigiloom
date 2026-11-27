extern alias HostMonitoring;

using System.Net.Http.Json;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostRetentionIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task OperationsPolicyControlsHostAuditRetention() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, token) =>
        {
            var operationsOptions = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new OperationsDbContext(operationsOptions)) await db.Database.MigrateAsync(token);
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(operationsDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var hostScheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(hostScheduling)) await db.Database.MigrateAsync(token);
            await using (var db = new HostMonitoringDbContext(hostOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = DateTimeOffset.UtcNow;
                var setting = new AgentSetting { UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now };
                db.Add(setting);
                db.Add(new AgentReplayPolicy());
                await db.SaveChangesAsync(token);
                foreach (var days in new[] { 40, 5 })
                    db.Add(new AgentSettingAudit { SettingId = setting.Id, ActorId = Guid.CreateVersion7(), OccurredAt = now.AddDays(-days),
                        EffectiveHeartbeatSeconds = 30, EffectiveSettingsPollSeconds = 300, EffectiveBatchIntervalSeconds = 60,
                        EffectiveRevision = "test", ChangeVersion = new byte[8] });
                await db.SaveChangesAsync(token);
            }
            var session = new SessionHandler();
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => session);
                    services.AddSingleton<IOutboxHandler>(new AuditOutboxHandler());
                });
            });
            using var hostClient = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
            const string replayPath = "/api/v1/agent-management/replay-policy";
            using (var anonymous = await hostClient.GetAsync(replayPath, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var cookieOptions = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, session.AccountId.ToString("D")),
                new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
            var cookie = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), IdentityConstants.ApplicationScheme));
            hostClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}");
            var replay = await hostClient.GetFromJsonAsync<AgentReplayPolicyView>(replayPath, token);
            Assert.AreEqual(AgentReplayPhase.Disabled, replay!.Phase);
            var change = new AgentReplayPolicyUpdate(AgentReplayPhase.Grace, replay.Version, true);
            using (var missingCsrf = await hostClient.PutAsJsonAsync(replayPath, change, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            var antiContext = new DefaultHttpContext { RequestServices = host.Services, User = new ClaimsPrincipal(identity) };
            antiContext.Request.Scheme = "https";
            var anti = host.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(antiContext);
            var antiCookie = antiContext.Response.Headers.SetCookie.Single()!.Split(';')[0];
            hostClient.DefaultRequestHeaders.Remove("Cookie");
            hostClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}; {antiCookie}");
            hostClient.DefaultRequestHeaders.Add("RequestVerificationToken", anti.RequestToken);
            using (var unconfirmed = await hostClient.PutAsJsonAsync(replayPath, change with { ConfirmedReceiversUpgraded = false }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            using (var saved = await hostClient.PutAsJsonAsync(replayPath, change, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                Assert.AreEqual(AgentReplayPhase.Grace, (await saved.Content.ReadFromJsonAsync<AgentReplayPolicyView>(cancellationToken: token))!.Phase);
            }
            using (var stale = await hostClient.PutAsJsonAsync(replayPath, change, token)) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            session.Role = "Read only";
            using (var denied = await hostClient.GetAsync(replayPath, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Testing");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Services:HostMonitoring:Url", "http://host");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(services =>
                {
                    HostWorkers.RemoveBackgroundWork(services);
                    services.AddHttpClient<RemoteHostAuditRetention>().ConfigurePrimaryHttpMessageHandler(() => host.Server.CreateHandler());
                });
            });
            using var client = operations.CreateClient();
            var settings = await client.GetFromJsonAsync<AuditRetentionSnapshot>("/internal/v1/history/retention", token);
            var actor = Guid.CreateVersion7();
            using var previewResponse = await client.PostAsJsonAsync("/internal/v1/history/retention/preview", new RetentionPreviewChange(actor, new(30, settings!.Version)), token);
            previewResponse.EnsureSuccessStatusCode();
            var preview = await previewResponse.Content.ReadFromJsonAsync<AuditRetentionPreview>(cancellationToken: token);
            Assert.AreEqual(1L, preview!.AffectedRecords);
            using var updated = await client.PutAsJsonAsync("/internal/v1/history/retention", new RetentionChange(actor, new(30, settings.Version, preview.ConfirmationToken)), token);
            updated.EnsureSuccessStatusCode();
            await using (var scope = operations.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AuditRetentionCleanup>().RunAsync(token);
            await using var check = new HostMonitoringDbContext(hostOptions);
            Assert.IsTrue((await check.Set<AgentSettingAudit>().SingleAsync(token)).OccurredAt > DateTimeOffset.UtcNow.AddDays(-30));
        }, outerToken), context.CancellationToken);

    private sealed class SessionHandler : HttpMessageHandler
    {
        public Guid AccountId { get; } = Guid.CreateVersion7();
        public string Role { get; set; } = "Administrator";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValidatedCookieResponse([
                new(ClaimTypes.NameIdentifier, AccountId.ToString("D"), ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY"),
                new(ClaimTypes.Role, Role, ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY")
            ])) });
    }

    private sealed class AuditOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken token) =>
            throw new NotSupportedException();
    }
}

extern alias GatewayHost;
extern alias HostMonitoring;
extern alias OperationsHost;
extern alias ResourcesHost;

using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.UpdateSecurity.AgentPackages;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Real service boundaries for the installed Agent lifecycle test.</summary>
internal sealed class DistributedAgentSite : IDisposable
{
    private readonly List<IDisposable> owned = [];
    public WebApplicationFactory<ResourcesHost::Program> Resources { get; private set; } = null!;
    public WebApplicationFactory<HostMonitoring::Program> Host { get; private set; } = null!;
    public WebApplicationFactory<OperationsHost::Program> Operations { get; private set; } = null!;
    public WebApplicationFactory<GatewayHost::Program> Gateway { get; private set; } = null!;
    public Uri GatewayAddress { get; private set; } = null!;

    public static async Task<DistributedAgentSite> StartAsync(string resourcesConnection, string hostConnection, string operationsConnection, X509Certificate2 certificate,
        AgentTransportGate gate, IPackageLibraryTransport packages, GitHubAgentReleases releases, CancellationToken token)
    {
        var site = new DistributedAgentSite();
        try
        {
            await MigrateAsync(new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(resourcesConnection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options), token);
            foreach (var connection in new[] { resourcesConnection, hostConnection, operationsConnection }.Distinct(StringComparer.Ordinal))
                await MigrateAsync(new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
                    .UseSqlServer(connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options), token);
            await using (var db = new HostMonitoringDbContext(new DbContextOptionsBuilder<HostMonitoringDbContext>()
                .UseSqlServer(hostConnection, sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options))
            {
                await db.Database.MigrateAsync(token);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                var policy = new AgentReplayPolicy();
                policy.InitializeFresh(now);
                policy.BeginCleanupCutover(now);
                db.ReplayPolicies.Add(policy);
                await db.SaveChangesAsync(token);
            }
            await MigrateAsync(new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
                .UseSqlServer(operationsConnection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options), token);
            await MigrateAsync(new LogAdmissionDbContext(new DbContextOptionsBuilder<LogAdmissionDbContext>()
                .UseSqlServer(operationsConnection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options), token);
            await MigrateAsync(new CentralLogsDbContext(new DbContextOptionsBuilder<CentralLogsDbContext>()
                .UseSqlServer(operationsConnection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory_CentralLogs", "logs")).Options), token);

            site.Resources = new WebApplicationFactory<ResourcesHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("ConnectionStrings:Resources", resourcesConnection);
                web.UseSetting("Vigiloom:Gateway:TrustedProxies:0", "127.0.0.1");
                web.ConfigureTestServices(services =>
                {
                    RemoveWorkers(services);
                    services.RemoveAll<IPackageLibraryTransport>();
                    services.AddSingleton(packages);
                    services.RemoveAll<GitHubAgentReleases>();
                    services.AddSingleton(releases);
                });
            });
            site.owned.Add(site.Resources);
            site.Resources.UseKestrel(0);
            var resources = site.Resources.CreateClient(); site.owned.Add(resources);

            site.Operations = new WebApplicationFactory<OperationsHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("ConnectionStrings:Operations", operationsConnection);
                web.UseSetting("Services:Resources:Url", resources.BaseAddress!.ToString());
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:EnableOperationsWorker", "false");
                web.ConfigureTestServices(RemoveWorkers);
            });
            site.owned.Add(site.Operations);
            site.Operations.UseKestrel(0);
            var operations = site.Operations.CreateClient(); site.owned.Add(operations);

            site.Host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("ConnectionStrings:HostMonitoring", hostConnection);
                web.UseSetting("Vigiloom:Gateway:TrustedProxies:0", "127.0.0.1");
                web.UseSetting("Services:Resources:Url", resources.BaseAddress!.ToString());
                web.UseSetting("Services:Operations:Url", operations.BaseAddress!.ToString());
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
            });
            site.owned.Add(site.Host);
            site.Host.UseKestrel(0);
            var host = site.Host.CreateClient(); site.owned.Add(host);
            site.Resources.Services.GetRequiredService<IConfiguration>()["Services:Host:Url"] = host.BaseAddress!.ToString();

            site.Gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                web.UseSetting("ReverseProxy:Clusters:resources:Destinations:resources:Address", resources.BaseAddress!.ToString());
                web.UseSetting("ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address", host.BaseAddress!.ToString());
                web.UseSetting("Vigiloom:Gateway:AgentIdentityAddress", host.BaseAddress!.ToString());
                web.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(gate));
            });
            site.owned.Add(site.Gateway);
            site.Gateway.UseKestrel(0);
            site.Gateway.StartServer();
            site.GatewayAddress = new Uri(site.Gateway.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) + "/");
            return site;
        }
        catch { site.Dispose(); throw; }
    }

    private static async Task MigrateAsync(DbContext db, CancellationToken token)
    {
        await using (db) await db.Database.MigrateAsync(token);
    }

    private static void RemoveWorkers(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService) &&
            (item.ImplementationType?.Name is "WorkDispatcher" or "LineAggregationWorker" ||
             item.ImplementationFactory?.Method.Name.Contains("AddHostedTemporalWorker", StringComparison.Ordinal) == true)).ToArray())
            services.Remove(descriptor);
    }

    public void Dispose()
    {
        for (var index = owned.Count - 1; index >= 0; index--) owned[index].Dispose();
    }
}

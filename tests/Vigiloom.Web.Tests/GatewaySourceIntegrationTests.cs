extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class GatewaySourceIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task DiscoveryThroughGatewayUsesRealSourceCredentialAndRevocation() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var web = PingTestSite.CreateFactory(database.ConnectionString);
            web.UseKestrel(0);
            await PingTestSite.InitializeAsync(web, token);
            var contexts = web.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using var scope = web.Services.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var source = await credentials.IssueAsync("collector", token);
            var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(host =>
                host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReverseProxy:Clusters:observability:Destinations:observability:Address"] = address,
                    ["Vigiloom:Gateway:SourceIdentityAddress"] = address
                })));
            using var client = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            var observation = new ServiceObservation("gateway-integration", "test", "Gateway integration");
            using (var missing = await client.PostAsJsonAsync("/api/v1/service-discovery/observations", observation, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", source.Secret);
            using (var accepted = await client.PostAsJsonAsync("/api/v1/service-discovery/observations", observation, token))
                Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            await using (var db = await contexts.CreateDbContextAsync(token))
                Assert.IsTrue(await db.Candidates.AnyAsync(candidate => candidate.ServiceKey == "gateway-integration", token));
            var resource = new Resource();
            resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "gateway-integration" } });
            resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "test" } });
            var metric = new Metric { Name = "gateway.test", Gauge = new Gauge { DataPoints = { new NumberDataPoint
                { TimeUnixNano = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000, AsDouble = 1 } } } };
            var request = new ExportMetricsServiceRequest { ResourceMetrics = { new ResourceMetrics
                { Resource = resource, ScopeMetrics = { new ScopeMetrics { Metrics = { metric } } } } } };
            using (var body = new ByteArrayContent(request.ToByteArray()))
            {
                body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                using var metrics = await client.PostAsync("/api/v1/otlp/v1/metrics", body, token);
                Assert.AreEqual(HttpStatusCode.OK, metrics.StatusCode);
                Assert.AreEqual("application/x-protobuf", metrics.Content.Headers.ContentType?.MediaType);
                Assert.AreEqual(1L, ExportMetricsServiceResponse.Parser.ParseFrom(await metrics.Content.ReadAsByteArrayAsync(token)).PartialSuccess.RejectedDataPoints);
            }
            Assert.IsTrue(await credentials.RevokeAsync(source.SourceId, token));
            using var revoked = await client.PostAsJsonAsync("/api/v1/service-discovery/observations", observation, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }, context.CancellationToken);
}

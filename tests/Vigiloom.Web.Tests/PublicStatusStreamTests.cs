using System.Net;
using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class PublicStatusStreamTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Subscription_DeadlineBeforeHeadersReturnsRetryableError() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var gate = new BlockDatabaseRead();
        var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(gate).Options;
        var contexts = new PooledDbContextFactory<ResourcesDbContext>(options);
        var clock = new FakeTimeProvider();
        using var admission = new PublicStreamAdmission(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), clock, TimeSpan.FromSeconds(30));
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var reader = new PublicReader(contexts, services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
            services.GetRequiredService<PingStore>(), services.GetRequiredService<HealthStatusReader>());
        var http = new DefaultHttpContext { RequestServices = services, RequestAborted = disconnect.Token };
        await using var body = new MemoryStream();
        http.Response.Body = body;
        var execution = new PublicStatusStream(PublicationResourceKind.Device, device.Id, reader, admission).ExecuteAsync(http);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            clock.Advance(TimeSpan.FromSeconds(3));
            await execution.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
            Assert.AreEqual("15", http.Response.Headers.RetryAfter.ToString());
            Assert.IsGreaterThan(0, body.Length);
            Assert.AreEqual(1, admission.Slots.CurrentCount);
        }
        finally
        {
            disconnect.Cancel();
            await execution;
        }
    }, context.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Subscription_CancellationReleasesBlockedResponseWrite(bool clientDisconnect) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            (await db.Devices.SingleAsync(x => x.Id == device.Id, token)).SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        var clock = new FakeTimeProvider();
        using var admission = new PublicStreamAdmission(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), clock);
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var scope = host.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.RequestAborted = disconnect.Token;
        http.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        await using var body = new BlockingResponseStream();
        http.Response.Body = body;
        var result = new PublicStatusStream(PublicationResourceKind.Device, device.Id, scope.ServiceProvider.GetRequiredService<PublicReader>(), admission);
        var execution = result.ExecuteAsync(http);
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            Assert.AreEqual(0, admission.Slots.CurrentCount);
            if (clientDisconnect) disconnect.Cancel();
            else clock.Advance(TimeSpan.FromSeconds(3));
            await execution.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.IsTrue(body.Canceled);
            Assert.AreEqual(1, admission.Slots.CurrentCount);
        }
        finally
        {
            disconnect.Cancel();
            await execution;
        }
    }, context.CancellationToken);

    [TestMethod]
    [DataRow("devices")]
    [DataRow("device-template-retired")]
    [DataRow("services")]
    [DataRow("groups")]
    [DataRow("group-member")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Subscription_ReauthorizesAfterRevocationAndReleasesCapacity(string kind) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var original = PingTestSite.CreateFactory(database.ConnectionString);
        var admission = new PublicStreamAdmission(1, TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(2), TimeProvider.System);
        using var host = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PublicStreamAdmission>();
            services.AddSingleton(admission);
        }));
        host.UseKestrel(0);
        host.StartServer();
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var contexts = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        var serviceContexts = host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("private-source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "private-service", "private-environment", "Private name", now);
        var service = new MonitoredService(candidate, now);
        service.SetPublication(true, "Public service", PublicationPolicy.BaselineVersion, now);
        var group = new Group("private-group", now);
        group.SetPublication(true, "Public group", PublicationPolicy.BaselineVersion, now);
        var template = new PublicationTemplate("Private template", true, true, now);
        if (kind == "device-template-retired")
        {
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token);
            db.Templates.Add(template);
            await db.SaveChangesAsync(token);
        }
        await using (var db = await serviceContexts.CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var saved = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            if (kind == "device-template-retired") saved.SetTemplatePublication("Public network", template.Id, false, now);
            else saved.SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, now);
            db.Groups.Add(group);
            db.ServiceGroups.Add(new ServiceGroup(group.Id, service.Id));
            await db.SaveChangesAsync(token);
        }

        using var client = new HttpClient { BaseAddress = host.ClientOptions.BaseAddress };
        var (id, publicName) = kind switch
        {
            "devices" or "device-template-retired" => (device.Id, "Public network"),
            "services" => (service.Id, "Public service"),
            _ => (group.Id, "Public group")
        };
        var routeKind = kind switch { "group-member" => "groups", "device-template-retired" => "devices", _ => kind };
        var url = $"/api/v1/public/{routeKind}/{id:D}/events";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var first = await ReadEventAsync(reader, token);
        Assert.AreEqual("status", first.Event);
        Assert.Contains(publicName, first.Data);
        Assert.DoesNotContain(device.Address, first.Data);
        Assert.DoesNotContain("private-", first.Data);

        using (var busy = await client.GetAsync(url, token))
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.IsNotNull(busy.Headers.RetryAfter);
        }
        if (kind == "device-template-retired")
        {
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token);
            (await db.Templates.SingleAsync(x => x.Id == template.Id, token)).Retire(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        else if (kind is "services" or "group-member")
        {
            await using var db = await serviceContexts.CreateDbContextAsync(token);
            (await db.Services.SingleAsync(x => x.Id == service.Id, token)).SetPublication(false, null, null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        else
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            if (kind == "devices") (await db.Devices.SingleAsync(x => x.Id == device.Id, token)).SetPublication(false, null, null, DateTimeOffset.UtcNow);
            else (await db.Groups.SingleAsync(x => x.Id == group.Id, token)).SetPublication(false, null, null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        (string Event, string Data) next;
        do { next = await ReadEventAsync(reader, wait.Token); } while (next.Event == "status");
        Assert.AreEqual("unavailable", next.Event);
        Assert.DoesNotContain(publicName, next.Data);
        Assert.DoesNotContain(device.Address, next.Data);
        Assert.IsNull(await reader.ReadLineAsync(wait.Token));
        Assert.IsTrue(await admission.Slots.WaitAsync(TimeSpan.FromSeconds(5), wait.Token));
        admission.Slots.Release();
        using var denied = await client.GetAsync(url, wait.Token);
        Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
    }, context.CancellationToken);

    private static async Task<(string Event, string Data)> ReadEventAsync(StreamReader reader, CancellationToken token)
    {
        string? eventName = null, data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) return (eventName ?? "", data ?? "");
            if (line.Length == 0 && eventName is not null) return (eventName, data ?? "");
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class BlockDatabaseRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class BlockingResponseStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
    }
}

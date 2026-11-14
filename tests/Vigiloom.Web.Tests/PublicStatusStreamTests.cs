using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.Resources.Persistence;
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
    public Task Subscription_DeadlineCancelsBlockedResponseWrite() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
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
            clock.Advance(TimeSpan.FromSeconds(3));
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
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Subscription_ReauthorizesAfterRevocationAndReleasesCapacity() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var original = PingTestSite.CreateFactory(database.ConnectionString);
        var admission = new PublicStreamAdmission(1, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeProvider.System);
        using var host = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PublicStreamAdmission>();
            services.AddSingleton(admission);
        }));
        host.UseKestrel(0);
        host.StartServer();
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var contexts = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            var saved = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            saved.SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }

        using var client = new HttpClient { BaseAddress = host.ClientOptions.BaseAddress };
        var url = $"/api/v1/public/devices/{device.Id:D}/events";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var first = await ReadEventAsync(reader, token);
        Assert.AreEqual("status", first.Event);
        Assert.Contains("Public network", first.Data);
        Assert.DoesNotContain(device.Address, first.Data);

        using (var busy = await client.GetAsync(url, token))
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.IsNotNull(busy.Headers.RetryAfter);
        }
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            (await db.Devices.SingleAsync(x => x.Id == device.Id, token)).SetPublication(false, null, null, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        (string Event, string Data) next;
        do { next = await ReadEventAsync(reader, wait.Token); } while (next.Event == "status");
        Assert.AreEqual("unavailable", next.Event);
        Assert.DoesNotContain("Public network", next.Data);
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

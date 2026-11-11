using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class PackageArchiveDownloaderTests(TestContext context)
{
    [TestMethod]
    public async Task Download_PublishesOnlyBytesMatchingSignedMetadata()
    {
        var content = "package content"u8.ToArray();
        var catalogue = Catalogue(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-package-test-{Guid.CreateVersion7():N}");
        try
        {
            using var client = new HttpClient(new Handler(content));
            var downloader = new PackageArchiveDownloader(new Uri("https://updates.example.test/packages/"), client);
            var path = await downloader.DownloadAsync(catalogue, catalogue.Packages[0], directory, TimeProvider.System, context.CancellationToken);
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(path, context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Download_DeletesTamperedPartialFileAndRejectsRevokedPackage()
    {
        var content = "package content"u8.ToArray();
        var catalogue = Catalogue(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-package-test-{Guid.CreateVersion7():N}");
        try
        {
            using var client = new HttpClient(new Handler("other content!!"u8.ToArray()));
            var downloader = new PackageArchiveDownloader(new Uri("https://updates.example.test/packages/"), client);
            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(catalogue, catalogue.Packages[0], directory, TimeProvider.System, context.CancellationToken));
            Assert.IsEmpty(Directory.EnumerateFiles(directory));
            var revoked = Catalogue(content, revoked: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadAsync(revoked, revoked.Packages[0], directory, TimeProvider.System, context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Download_RejectsUnknownLengthShortAndOversizedBodies()
    {
        var content = "package content"u8.ToArray();
        var catalogue = Catalogue(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-package-test-{Guid.CreateVersion7():N}");
        try
        {
            foreach (var body in new[] { content[..^1], content.Concat(new byte[] { 1 }).ToArray() })
            {
                using var client = new HttpClient(new UnknownLengthHandler(body));
                var downloader = new PackageArchiveDownloader(new Uri("https://updates.example.test/packages/"), client);
                await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(catalogue, catalogue.Packages[0], directory, TimeProvider.System, context.CancellationToken));
                Assert.IsEmpty(Directory.EnumerateFiles(directory));
            }
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Download_RetriesTransientServerFailure()
    {
        var content = "package content"u8.ToArray();
        var catalogue = Catalogue(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-package-test-{Guid.CreateVersion7():N}");
        try
        {
            var handler = new TransientHandler(content);
            using var client = new HttpClient(handler);
            var downloader = new PackageArchiveDownloader(new Uri("https://updates.example.test/packages/"), client);
            var path = await downloader.DownloadAsync(catalogue, catalogue.Packages[0], directory, TimeProvider.System, context.CancellationToken);
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(path, context.CancellationToken));
            Assert.AreEqual(2, handler.Requests);
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Download_DoesNotRetryBeforeLongRetryAfter()
    {
        var content = "package content"u8.ToArray();
        var catalogue = Catalogue(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-package-test-{Guid.CreateVersion7():N}");
        try
        {
            var handler = new RateLimitHandler();
            using var client = new HttpClient(handler);
            var downloader = new PackageArchiveDownloader(new Uri("https://updates.example.test/packages/"), client);
            var error = await Assert.ThrowsAsync<PackageSourceRetryException>(() => downloader.DownloadAsync(catalogue, catalogue.Packages[0], directory, TimeProvider.System, context.CancellationToken));
            Assert.AreEqual(TimeSpan.FromMinutes(2), error.RetryAfter);
            Assert.AreEqual(1, handler.Requests);
        }
        finally { TestDirectories.Delete(directory); }
    }

    private static PackageCatalogue Catalogue(byte[] content, bool revoked = false)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var json = $$"""{"schema":1,"packages":[{"version":"1.2.3","rid":"win-x64","size":{{content.Length}},"sha256":"{{hash}}","manifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","revoked":{{revoked.ToString().ToLowerInvariant()}}}]}""";
        var now = DateTimeOffset.UtcNow;
        return PackageCatalogue.Parse(new VerifiedCatalogue(Encoding.UTF8.GetBytes(json), now.AddHours(1)), now);
    }

    private sealed class Handler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }

    private sealed class UnknownLengthHandler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(data) });
    }

    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(data));
    }

    private sealed class TransientHandler(byte[] data) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Requests == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    private sealed class RateLimitHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return Task.FromResult(response);
        }
    }
}

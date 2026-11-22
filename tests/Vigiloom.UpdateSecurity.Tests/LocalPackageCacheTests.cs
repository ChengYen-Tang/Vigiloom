using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class LocalPackageCacheTests(TestContext context)
{
    [TestMethod]
    public async Task Open_ReusesVerifiedFile()
    {
        var content = "package"u8.ToArray();
        var manifest = Manifest(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-cache-test-{Guid.CreateVersion7():N}");
        try
        {
            var handler = new Handler(content);
            using var client = new HttpClient(handler);
            var downloader = new PackageArchiveDownloader(new Uri("https://updates.example.test/packages/"), client);
            var cache = new LocalPackageCache(directory, downloader, TimeProvider.System);
            await using (var first = await cache.OpenManifestAsync(manifest, context.CancellationToken))
            using (var reader = new StreamReader(first)) Assert.AreEqual("package", await reader.ReadToEndAsync(context.CancellationToken));
            await using (var second = await cache.OpenManifestAsync(manifest, context.CancellationToken)) Assert.AreEqual(content.Length, second.Length);
            Assert.AreEqual(1, handler.Requests);

        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Open_ReplacesCorruptCacheAndCleansInterruptedDownload()
    {
        var content = "package"u8.ToArray();
        var manifest = Manifest(content);
        var entry = manifest;
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-cache-test-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, entry.Sha256 + ".pkg"), "invalid", context.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "interrupted.download"), "partial", context.CancellationToken);
            using var client = new HttpClient(new Handler(content));
            var cache = new LocalPackageCache(directory, new PackageArchiveDownloader(new Uri("https://updates.example.test/"), client), TimeProvider.System);
            await using var stream = await cache.OpenManifestAsync(entry, context.CancellationToken);
            Assert.AreEqual(content.Length, stream.Length);
            Assert.IsEmpty(Directory.EnumerateFiles(directory, "*.download"));
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Open_ConcurrentMissesDownloadOnce()
    {
        var content = "package"u8.ToArray();
        var manifest = Manifest(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-cache-test-{Guid.CreateVersion7():N}");
        try
        {
            var handler = new Handler(content);
            using var client = new HttpClient(handler);
            var cache = new LocalPackageCache(directory, new PackageArchiveDownloader(new Uri("https://updates.example.test/"), client), TimeProvider.System);
            var streams = await Task.WhenAll(
                cache.OpenManifestAsync(manifest, context.CancellationToken),
                cache.OpenManifestAsync(manifest, context.CancellationToken));
            try { Assert.AreEqual(1, handler.Requests); }
            finally { foreach (var stream in streams) await stream.DisposeAsync(); }
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Open_ReplacesCorruptFileWhileAnotherReaderKeepsItsHandle()
    {
        var content = "package"u8.ToArray();
        var manifest = Manifest(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-cache-test-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, manifest.Sha256 + ".pkg");
            await File.WriteAllTextAsync(path, "corrupt", context.CancellationToken);
            await using var existingReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var client = new HttpClient(new Handler(content));
            var cache = new LocalPackageCache(directory, new PackageArchiveDownloader(new Uri("https://updates.example.test/"), client), TimeProvider.System);
            await using var repaired = await cache.OpenManifestAsync(manifest, context.CancellationToken);
            using var newReader = new StreamReader(repaired);
            Assert.AreEqual("package", await newReader.ReadToEndAsync(context.CancellationToken));
            using var oldReader = new StreamReader(existingReader);
            Assert.AreEqual("corrupt", await oldReader.ReadToEndAsync(context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Open_WaitsForExistingCacheMutationLock()
    {
        var content = "package"u8.ToArray();
        var manifest = Manifest(content);
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-cache-test-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var held = new FileStream(Path.Combine(directory, ".cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var handler = new Handler(content);
            using var client = new HttpClient(handler);
            var cache = new LocalPackageCache(directory, new PackageArchiveDownloader(new Uri("https://updates.example.test/"), client), TimeProvider.System);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var pending = cache.OpenManifestAsync(manifest, stop.Token);
            try
            {
                await Task.Delay(250, context.CancellationToken);
                Assert.IsFalse(pending.IsCompleted);
                Assert.AreEqual(0, handler.Requests);
                await held.DisposeAsync();
                await using var stream = await pending;
                Assert.AreEqual(1, handler.Requests);
            }
            finally
            {
                await held.DisposeAsync();
                if (!pending.IsCompleted) stop.Cancel();
                try { await (await pending).DisposeAsync(); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        finally { TestDirectories.Delete(directory); }
    }

    private static PackageManifest Manifest(byte[] content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var json = $$"""{"schema":1,"version":"1.2.3","rid":"win-x64","size":{{content.Length}},"sha256":"{{hash}}"}""";
        return PackageManifest.Parse(new VerifiedManifest(Encoding.UTF8.GetBytes(json)));
    }

    private sealed class Handler(byte[] data) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }
}

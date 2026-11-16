using System.Net;
using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class CentralPackageAcquirerTests(TestContext context)
{
    private const string ManifestHash = "50abf0641e53ea3c0a39aa72c37e4d2e360e7b890abaff62537d3d3613d0e1d1";

    [TestMethod]
    public async Task VerifiesPublisherManifestAndDownloadsMatchingArchive()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"central-package-{Guid.NewGuid():N}");
        try
        {
            var trust = Path.Combine(root, "trust");
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PythonTuf");
            await new LocalTrustStore(trust).ProvisionAsync(await File.ReadAllBytesAsync(Path.Combine(fixture, "root.json"), context.CancellationToken), context.CancellationToken);
            var handler = new FixtureHandler(fixture);
            using var http = new HttpClient(handler);
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(cache);
            var old = Path.Combine(cache, new string('a', 64) + ".pkg");
            var unrelated = Path.Combine(cache, "unrelated.txt");
            await File.WriteAllTextAsync(old, "olddata", context.CancellationToken);
            await File.WriteAllTextAsync(unrelated, "keep", context.CancellationToken);
            var source = new CentralPackageAcquirer(new Uri("https://example.test/"), trust, cache, 7, TimeProvider.System, http);
            await using (var package = await source.AcquireAsync(ManifestHash, "1.0.0", "win-x64", "1.0.0", "1.0.0", "vla_test", context.CancellationToken))
            using (var reader = new StreamReader(package.Archive, Encoding.UTF8, leaveOpen: true))
                Assert.AreEqual("package", await reader.ReadToEndAsync(context.CancellationToken));
            Assert.IsFalse(File.Exists(old));
            Assert.IsTrue(File.Exists(unrelated));
            await using (var repeated = await source.AcquireAsync(ManifestHash, "1.0.0", "win-x64", "1.0.0", "1.0.0", "vla_test", context.CancellationToken))
                Assert.AreEqual(7, repeated.Archive.Length);
            Assert.AreEqual(1, handler.Paths.Count(x => x.StartsWith("/api/v1/agent/update/packages/", StringComparison.Ordinal)));
            Assert.IsTrue(handler.Paths.Any(x => x.StartsWith("/api/v1/agent/update/metadata/", StringComparison.Ordinal)));
            Assert.IsTrue(handler.Paths.Any(x => x.StartsWith("/api/v1/agent/update/targets/", StringComparison.Ordinal)));
            Assert.IsTrue(handler.Paths.Any(x => x.StartsWith("/api/v1/agent/update/packages/", StringComparison.Ordinal)));
            Assert.IsTrue(handler.AllAuthenticated);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RejectsIncompatibleReleaseBeforeDownloadingArchive()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"central-package-{Guid.NewGuid():N}");
        try
        {
            var trust = Path.Combine(root, "trust");
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PythonTuf");
            await new LocalTrustStore(trust).ProvisionAsync(await File.ReadAllBytesAsync(Path.Combine(fixture, "root.json"), context.CancellationToken), context.CancellationToken);
            var handler = new FixtureHandler(fixture);
            using var http = new HttpClient(handler);
            var source = new CentralPackageAcquirer(new Uri("https://example.test/"), trust, Path.Combine(root, "cache"), 1024, TimeProvider.System, http);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => source.AcquireAsync(ManifestHash, "1.0.0", "linux-x64", "1.0.0", "1.0.0", "vla_test", context.CancellationToken));
            Assert.IsFalse(handler.Paths.Any(x => x.StartsWith("/api/v1/agent/update/packages/", StringComparison.Ordinal)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RejectsCorruptedArchiveAndRemovesPartialDownload()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"central-package-{Guid.NewGuid():N}");
        try
        {
            var trust = Path.Combine(root, "trust");
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PythonTuf");
            await new LocalTrustStore(trust).ProvisionAsync(await File.ReadAllBytesAsync(Path.Combine(fixture, "root.json"), context.CancellationToken), context.CancellationToken);
            var handler = new FixtureHandler(fixture) { CorruptArchive = true };
            using var http = new HttpClient(handler);
            var cache = Path.Combine(root, "cache");
            var source = new CentralPackageAcquirer(new Uri("https://example.test/"), trust, cache, 1024, TimeProvider.System, http);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => source.AcquireAsync(ManifestHash, "1.0.0", "win-x64", "1.0.0", "1.0.0", "vla_test", context.CancellationToken));
            Assert.IsEmpty(Directory.GetFiles(cache, "*.pkg"));
            Assert.IsEmpty(Directory.GetFiles(cache, "*.download"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class FixtureHandler(string fixture) : HttpMessageHandler
    {
        public bool CorruptArchive { get; init; }
        public List<string> Paths { get; } = [];
        public bool AllAuthenticated { get; private set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            AllAuthenticated &= request.Headers.Authorization is { Scheme: "Bearer", Parameter: "vla_test" };
            if (path.StartsWith("/api/v1/agent/update/packages/", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(CorruptArchive ? "corrupt" : "package")) };
            var name = path.StartsWith("/api/v1/agent/update/targets/", StringComparison.Ordinal) ? "manifest.json" : Path.GetFileName(path);
            var file = Path.Combine(fixture, name);
            return File.Exists(file) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(file, token)) }
                : new(HttpStatusCode.NotFound);
        }
    }
}

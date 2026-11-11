using System.Security.Cryptography;
using System.Text;
using System.Net;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class TrustedManifestReaderTests(TestContext context)
{
    [TestMethod]
    public async Task Read_AuthenticatesOnlyTheAddressedManifest()
    {
        var bytes = "signed manifest"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var repo = new TestRepository(key, catalogue: bytes, targetName: hash + ".json");
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-manifest-test-{Guid.CreateVersion7():N}");
        try
        {
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(repo.Root(1, key), context.CancellationToken);
            var uri = new Uri("https://updates.example.test/");
            await using var lease = await store.AcquireAsync(context.CancellationToken);
            var manifest = await new TrustedManifestReader(uri, uri, repo).ReadAsync(lease, hash, context.CancellationToken);
            CollectionAssert.AreEqual(bytes, manifest.Content.ToArray());
            await Assert.ThrowsAsync<ArgumentException>(() => new TrustedManifestReader(uri, uri, repo).ReadAsync(lease, "../catalog", context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Manifest_BindsReleaseAndChecksCompatibility()
    {
        var archive = "actual archive"u8.ToArray();
        var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var bytes = Encoding.UTF8.GetBytes($$"""{"schema":1,"version":"2.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{archiveHash}}","minimumUpdaterVersion":"1.1.0","minimumAgentVersion":"1.0.0","maximumAgentVersion":"2.0.0"}""");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var repo = new TestRepository(key, catalogue: bytes, targetName: hash + ".json");
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-manifest-test-{Guid.CreateVersion7():N}");
        try
        {
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(repo.Root(1, key), context.CancellationToken);
            var uri = new Uri("https://updates.example.test/");
            await using var lease = await store.AcquireAsync(context.CancellationToken);
            var verified = await new TrustedManifestReader(uri, uri, repo).ReadAsync(lease, hash, context.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var manifest = PackageManifest.Parse(verified, now);
            var json = $$"""{"schema":1,"packages":[{"version":"2.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{archiveHash}}","manifestSha256":"{{hash}}","revoked":false}]}""";
            var catalogue = PackageCatalogue.Parse(new VerifiedCatalogue(Encoding.UTF8.GetBytes(json), now.AddMinutes(1)), now);
            Assert.IsTrue(manifest.Matches(catalogue.Packages[0]));
            var changedArchive = json.Replace(archiveHash, new string('a', 64), StringComparison.Ordinal);
            var changedManifest = json.Replace(hash, new string('b', 64), StringComparison.Ordinal);
            var changedSize = json.Replace($"\"size\":{archive.Length}", $"\"size\":{archive.Length + 1}", StringComparison.Ordinal);
            foreach (var changed in new[] { changedArchive, changedManifest, changedSize })
            {
                var other = PackageCatalogue.Parse(new VerifiedCatalogue(Encoding.UTF8.GetBytes(changed), now.AddMinutes(1)), now);
                Assert.IsFalse(manifest.Matches(other.Packages[0]));
            }
            Assert.IsTrue(manifest.IsCompatible("win-x64", "1.1.0", "1.5.0", now));
            Assert.IsFalse(manifest.IsCompatible("linux-x64", "1.1.0", "1.5.0", now));
            Assert.IsFalse(manifest.IsCompatible("win-x64", "1.0.0", "1.5.0", now));
            Assert.IsFalse(manifest.IsCompatible("win-x64", "1.1.0", "3.0.0", now));
            Assert.IsFalse(manifest.IsCompatible("win-x64", "1.1.0", null, now));
            Assert.IsFalse(manifest.IsCompatible("win-x64", "1.1.0", "1.5.0", manifest.Expires));
            using var client = new HttpClient(new ArchiveHandler(archive));
            var downloader = new PackageArchiveDownloader(uri, client, "agent-credential");
            var path = await downloader.DownloadManifestAsync(manifest, directory, TimeProvider.System, context.CancellationToken);
            CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(path, context.CancellationToken));
            var cache = new LocalPackageCache(Path.Combine(directory, "cache"), 1024, downloader, TimeProvider.System);
            await using var cached = await cache.OpenManifestAsync(manifest, context.CancellationToken);
            using var output = new MemoryStream();
            await cached.CopyToAsync(output, context.CancellationToken);
            CollectionAssert.AreEqual(archive, output.ToArray());
        }
        finally { TestDirectories.Delete(directory); }
    }

    private sealed class ArchiveHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("agent-credential", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    [TestMethod]
    public async Task Read_RejectsSignedTargetWhoseNameDoesNotMatchContent()
    {
        var bytes = "signed manifest"u8.ToArray();
        var falseHash = new string('0', 64);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var repo = new TestRepository(key, catalogue: bytes, targetName: falseHash + ".json");
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-manifest-test-{Guid.CreateVersion7():N}");
        try
        {
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(repo.Root(1, key), context.CancellationToken);
            var uri = new Uri("https://updates.example.test/");
            await using var lease = await store.AcquireAsync(context.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => new TrustedManifestReader(uri, uri, repo).ReadAsync(lease, falseHash, context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }
}

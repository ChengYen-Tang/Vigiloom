using Tuf;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class PythonTufFixtureTests(TestContext context)
{
    [TestMethod]
    public async Task OfficialPublisherMetadata_VerifiesCatalogueAndIndividualManifest()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PythonTuf");
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-interop-test-{Guid.CreateVersion7():N}");
        try
        {
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(await File.ReadAllBytesAsync(Path.Combine(fixture, "root.json"), context.CancellationToken), context.CancellationToken);
            await using var lease = await store.AcquireAsync(context.CancellationToken);
            var repository = new FixtureRepository(fixture);
            var uri = new Uri("https://updates.example.test/");
            var verified = await new TrustedCatalogueReader(uri, uri, repository).ReadAsync(lease, context.CancellationToken);
            var catalogue = PackageCatalogue.Parse(verified, DateTimeOffset.UtcNow);
            Assert.HasCount(1, catalogue.Packages);
            var target = await new TrustedManifestReader(uri, uri, repository).ReadAsync(lease, catalogue.Packages[0].ManifestSha256, context.CancellationToken);
            Assert.IsTrue(PackageManifest.Parse(target, DateTimeOffset.UtcNow).Matches(catalogue.Packages[0]));
        }
        finally { TestDirectories.Delete(directory); }
    }

    private sealed class FixtureRepository(string directory) : ITufRepository
    {
        public Task<byte[]?> FetchMetadataAsync(string role, int? version = null, CancellationToken cancellationToken = default) =>
            ReadAsync(version is null ? role + ".json" : version + "." + role + ".json", cancellationToken);
        public Task<byte[]?> FetchTargetAsync(string targetPath, CancellationToken cancellationToken = default) =>
            ReadAsync(targetPath == "catalog.json" ? targetPath : "manifest.json", cancellationToken);
        private async Task<byte[]?> ReadAsync(string name, CancellationToken token)
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
        }
    }
}

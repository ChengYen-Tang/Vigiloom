using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tuf;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class TrustedCatalogueReaderTests(TestContext context)
{
    private static readonly Uri Repository = new("https://updates.example.test/");

    [TestMethod]
    public async Task Read_VerifiesCatalogueAndPersistsAcceptedMetadata()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var repo = new TestRepository(signer);
        var lease = new TestLease(repo.Root(1, signer));
        var reader = new TrustedCatalogueReader(Repository, Repository, repo);
        var result = await reader.ReadAsync(lease, context.CancellationToken);
        Assert.AreEqual("catalogue", Encoding.UTF8.GetString(result.Content.Span));
        Assert.IsGreaterThan(DateTimeOffset.UtcNow, result.Expires);
        Assert.IsTrue(lease.Data.ContainsKey("timestamp"));
        Assert.IsTrue(lease.Data.ContainsKey("snapshot"));
        Assert.IsTrue(lease.Data.ContainsKey("targets"));
    }

    [TestMethod]
    public async Task Read_AuthenticatesAndParsesPackageCatalogue()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = """{"schema":1,"packages":[{"version":"1.2.3","rid":"linux-x64","size":123,"sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","manifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","revoked":true}]}""";
        var repo = new TestRepository(signer, catalogue: Encoding.UTF8.GetBytes(json));
        var lease = new SnapshotLease(repo.Root(1, signer));
        var verified = await new TrustedCatalogueReader(Repository, Repository, repo).ReadAsync(lease, context.CancellationToken);
        var catalogue = PackageCatalogue.Parse(verified, DateTimeOffset.UtcNow);
        Assert.HasCount(1, catalogue.Packages);
        Assert.IsTrue(catalogue.Packages[0].Revoked);
    }

    [TestMethod]
    public async Task Read_AfterRootRotationAndMissingTarget_DoesNotTrustOldKeyAgain()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldRepo = new TestRepository(oldKey);
        var lease = new TestLease(oldRepo.Root(1, oldKey));
        var newRepo = new TestRepository(newKey) { RotatedRoot = oldRepo.Root(2, newKey, oldKey), MissingTarget = true };
        var reader = new TrustedCatalogueReader(Repository, Repository, newRepo);
        await Assert.ThrowsAsync<TufException>(() => reader.ReadAsync(lease, context.CancellationToken));
        using (var root = JsonDocument.Parse(lease.Data["root"]))
            Assert.AreEqual(2, root.RootElement.GetProperty("signed").GetProperty("version").GetInt32());
        // The next process starts from the persisted root, not the original bootstrap.
        reader = new TrustedCatalogueReader(Repository, Repository, oldRepo);
        await Assert.ThrowsAsync<TufException>(() => reader.ReadAsync(lease, context.CancellationToken));
    }

    [TestMethod]
    public async Task Read_DoesNotReturnCatalogueWhenTrustPersistenceFails()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var repo = new TestRepository(signer);
        var lease = new TestLease(repo.Root(1, signer)) { FailWrites = true };
        var reader = new TrustedCatalogueReader(Repository, Repository, repo);
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(lease, context.CancellationToken));
    }

    [TestMethod]
    public async Task Read_DoesNotReturnCatalogueAfterLeaseIsLost()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var repo = new TestRepository(signer);
        var lease = new TestLease(repo.Root(1, signer));
        var reader = new TrustedCatalogueReader(Repository, Repository, repo);
        await reader.ReadAsync(lease, context.CancellationToken);
        lease.Lost = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(lease, context.CancellationToken));
    }

    [TestMethod]
    public async Task Read_RotatedOnlineKeysRecoverFromFastForwardThroughDurableSnapshot()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldRepo = new TestRepository(oldKey, 100);
        var lease = new SnapshotLease(oldRepo.Root(1, oldKey));
        await new TrustedCatalogueReader(Repository, Repository, oldRepo).ReadAsync(lease, context.CancellationToken);
        var rotated = new TestRepository(newKey) { RotatedRoot = oldRepo.Root(2, newKey, oldKey) };
        var result = await new TrustedCatalogueReader(Repository, Repository, rotated).ReadAsync(lease, context.CancellationToken);
        Assert.AreEqual("catalogue", Encoding.UTF8.GetString(result.Content.Span));
        using var timestamp = JsonDocument.Parse(lease.LoadMetadata("timestamp")!);
        Assert.AreEqual(1, timestamp.RootElement.GetProperty("signed").GetProperty("version").GetInt32());
    }

    [TestMethod]
    public async Task Read_WithoutKeyRotationRejectsFastForwardRollback()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldRepo = new TestRepository(key, 100);
        var lease = new SnapshotLease(oldRepo.Root(1, key));
        await new TrustedCatalogueReader(Repository, Repository, oldRepo).ReadAsync(lease, context.CancellationToken);
        var rolledBack = new TestRepository(key) { RotatedRoot = oldRepo.Root(2, key) };
        await Assert.ThrowsAsync<TufException>(() => new TrustedCatalogueReader(Repository, Repository, rolledBack).ReadAsync(lease, context.CancellationToken));
    }

    private sealed class SnapshotLease(byte[] root) : ITrustMetadataLease
    {
        private TrustMetadataSnapshot snapshot = TrustMetadataSnapshot.Provision(root);
        public byte[]? LoadMetadata(string role) => snapshot.Read(role);
        public void StoreMetadata(string role, ReadOnlySpan<byte> data) => snapshot = snapshot.WithMetadata(role, data);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task VerifyHeldAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestLease(byte[] root) : ITrustMetadataLease
    {
        public Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal) { ["root"] = root };
        public bool FailWrites { get; set; }
        public bool Lost { get; set; }
        public byte[]? LoadMetadata(string role) => Data.GetValueOrDefault(role);
        public void StoreMetadata(string role, ReadOnlySpan<byte> data)
        {
            if (FailWrites) throw new IOException("Cannot persist accepted trust metadata.");
            Data[role] = data.ToArray();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task VerifyHeldAsync(CancellationToken cancellationToken = default) => Lost
            ? Task.FromException(new InvalidOperationException("Lease lost.")) : Task.CompletedTask;
    }

}

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Tuf;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class LocalTrustStoreTests(TestContext context)
{
    [TestMethod]
    public async Task Store_PersistsSignedMetadataAndDoesNotReinitialize()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-trust-test-{Guid.CreateVersion7():N}");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var repository = new TestRepository(key);
            var root = repository.Root(1, key);
            var store = new LocalTrustStore(directory);
            await Assert.ThrowsAsync<SqliteException>(async () => await store.AcquireAsync(context.CancellationToken));
            await store.ProvisionAsync(root, context.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => store.ProvisionAsync(root, context.CancellationToken));
            var uri = new Uri("https://updates.example.test/");
            await using (var lease = await store.AcquireAsync(context.CancellationToken))
                await new TrustedCatalogueReader(uri, uri, repository).ReadAsync(lease, context.CancellationToken);
            await using (var lease = await store.AcquireAsync(context.CancellationToken))
            {
                CollectionAssert.AreEqual(root, lease.LoadMetadata("root"));
                Assert.IsNotNull(lease.LoadMetadata("timestamp"));
                Assert.IsNotNull(lease.LoadMetadata("snapshot"));
                Assert.IsNotNull(lease.LoadMetadata("targets"));
                await lease.VerifyHeldAsync(context.CancellationToken);
            }
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Store_RejectsCorruptStateAndRollback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-trust-test-{Guid.CreateVersion7():N}");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var repository = new TestRepository(key);
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(repository.Root(1, key), context.CancellationToken);
            var uri = new Uri("https://updates.example.test/");
            await using (var lease = await store.AcquireAsync(context.CancellationToken))
            {
                await new TrustedCatalogueReader(uri, uri, repository).ReadAsync(lease, context.CancellationToken);
                lease.StoreMetadata("root", repository.Root(2, key));
                Assert.ThrowsExactly<InvalidDataException>(() => lease.StoreMetadata("root", repository.Root(1, key)));
            }
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "trust.sqlite")};Pooling=False"))
            {
                await db.OpenAsync(context.CancellationToken);
                await using var command = db.CreateCommand();
                command.CommandText = "UPDATE trust SET digest = zeroblob(32)";
                await command.ExecuteNonQueryAsync(context.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.AcquireAsync(context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Store_RejectsOversizedBlobBeforeLoadingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-trust-test-{Guid.CreateVersion7():N}");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var repository = new TestRepository(key);
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(repository.Root(1, key), context.CancellationToken);
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "trust.sqlite")};Pooling=False"))
            {
                await db.OpenAsync(context.CancellationToken);
                await using var command = db.CreateCommand();
                command.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE trust SET snapshot = zeroblob(6291457)";
                await command.ExecuteNonQueryAsync(context.CancellationToken);
            }
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await store.AcquireAsync(context.CancellationToken));
            Assert.Contains("invalid size or type", error.Message);
        }
        finally { TestDirectories.Delete(directory); }
    }

    [TestMethod]
    public async Task Store_PersistsRotatedRootBeforeLaterDownloadFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-trust-test-{Guid.CreateVersion7():N}");
        try
        {
            using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var oldRepository = new TestRepository(oldKey);
            var nextRepository = new TestRepository(newKey) { RotatedRoot = oldRepository.Root(2, newKey, oldKey), MissingTarget = true };
            var store = new LocalTrustStore(directory);
            await store.ProvisionAsync(oldRepository.Root(1, oldKey), context.CancellationToken);
            var uri = new Uri("https://updates.example.test/");
            await using (var lease = await store.AcquireAsync(context.CancellationToken))
                await Assert.ThrowsAsync<TufException>(() => new TrustedCatalogueReader(uri, uri, nextRepository).ReadAsync(lease, context.CancellationToken));
            await using (var lease = await store.AcquireAsync(context.CancellationToken))
                await Assert.ThrowsAsync<TufException>(() => new TrustedCatalogueReader(uri, uri, oldRepository).ReadAsync(lease, context.CancellationToken));
        }
        finally { TestDirectories.Delete(directory); }
    }
}

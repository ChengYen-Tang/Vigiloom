using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.UpdateSecurity.Tests;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class UpdateTrustStoreSqlTests(TestContext testContext)
{
    [TestMethod]
    public Task TrustState_PersistsAndRejectsRollbackAndCorruption() => WithDatabase(async (factory, token) =>
    {
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcquireAsync(token));
        await store.ProvisionAsync(Envelope("root", 1), token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ProvisionAsync(Envelope("root", 1), token));
        await using (var lease = await store.AcquireAsync(token))
            lease.StoreMetadata("timestamp", Envelope("timestamp", 2));
        await using (var lease = await store.AcquireAsync(token))
        {
            CollectionAssert.AreEqual(Envelope("timestamp", 2), lease.LoadMetadata("timestamp"));
            Assert.ThrowsExactly<InvalidDataException>(() => lease.StoreMetadata("timestamp", Envelope("timestamp", 1)));
        }
        await using (var context = factory.CreateDbContext())
            await context.Database.ExecuteSqlRawAsync("UPDATE [agent].[UpdateTrustStates] SET [Digest] = 0x0000000000000000000000000000000000000000000000000000000000000000", token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AcquireAsync(token));
    });

    [TestMethod]
    public Task TrustLock_SerializesSessionsAndReleasesOnDispose() => WithDatabase(async (factory, token) =>
    {
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        await store.ProvisionAsync(Envelope("root", 1), token);
        await using var first = await store.AcquireAsync(token);
        using var stopPending = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = store.AcquireAsync(stopPending.Token);
        try
        {
            await Task.Delay(500, token);
            Assert.IsFalse(pending.IsCompleted, "A second session must wait for the first lock.");
            await first.DisposeAsync();
            await using var next = await pending;
            await next.VerifyHeldAsync(token);
            Assert.IsNotNull(next.LoadMetadata("root"));
        }
        finally
        {
            await first.DisposeAsync();
            if (!pending.IsCompleted) stopPending.Cancel();
            try { await (await pending).DisposeAsync(); }
            catch (Exception) when (stopPending.IsCancellationRequested) { }
        }
    });

    [TestMethod]
    public Task TrustLock_DetectsKilledSqlSession() => WithDatabase(async (factory, token) =>
    {
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        await store.ProvisionAsync(Envelope("root", 1), token);
        var lease = await store.AcquireAsync(token);
        try
        {
            await using var context = factory.CreateDbContext();
            await context.Database.OpenConnectionAsync(token);
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT DISTINCT request_session_id FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND resource_database_id = DB_ID() AND request_mode = 'X'";
            var session = Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(0, session);
            command.CommandText = $"KILL {session}"; // Session id comes from SQL Server, not external input.
            await command.ExecuteNonQueryAsync(token);
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => lease.VerifyHeldAsync(token));
        }
        finally
        {
            try { await lease.DisposeAsync(); }
            catch (Microsoft.Data.SqlClient.SqlException) { /* KILL already ended the SQL session. */ }
        }
    });

    [TestMethod]
    public Task Catalogue_PublishesSignedEntriesAndRejectsChangedReleaseAtomically() => WithDatabase(async (factory, token) =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        var sourceVersion = await SourceVersion(factory, token);
        var firstRepo = new TestRepository(key, catalogue: Catalogue("aa", false));
        await store.ProvisionAsync(firstRepo.Root(1, key), token);
        var uri = new Uri("https://updates.example.test/");
        await store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, firstRepo), TimeProvider.System, sourceVersion, cancellationToken: token);
        await using (var db = factory.CreateDbContext())
        {
            var package = await db.Packages.SingleAsync(token);
            Assert.IsTrue(package.Listed);
            Assert.IsFalse(package.Revoked);
        }
        var revokedRepo = new TestRepository(key, 2, Catalogue("aa", true), metadataVersion: 2);
        await store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, revokedRepo), TimeProvider.System, sourceVersion, cancellationToken: token);
        var changedRepo = new TestRepository(key, 3, Catalogue("bb", false), metadataVersion: 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, changedRepo), TimeProvider.System, sourceVersion, cancellationToken: token));
        await using (var db = factory.CreateDbContext())
        {
            var package = await db.Packages.SingleAsync(token);
            Assert.IsFalse(package.Listed, "New trust metadata must invalidate the old catalogue when publication fails.");
            Assert.IsTrue(package.Revoked);
            Assert.AreEqual(string.Concat(Enumerable.Repeat("aa", 32)), package.Sha256);
        }
    });

    [TestMethod]
    public Task Catalogue_TrustAdvanceAndInvalidationCommitTogetherAndRecover() => WithDatabase(async (factory, token) =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        var sourceVersion = await SourceVersion(factory, token);
        var first = new TestRepository(key, catalogue: Catalogue("aa", false));
        var uri = new Uri("https://updates.example.test/");
        await store.ProvisionAsync(first.Root(1, key), token);
        await store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, first), TimeProvider.System, sourceVersion, cancellationToken: token);
        first.MissingTarget = true;
        await Assert.ThrowsAsync<Tuf.TufException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, first), TimeProvider.System, sourceVersion, cancellationToken: token));
        await using (var db = factory.CreateDbContext()) Assert.IsTrue((await db.Packages.SingleAsync(token)).Listed);
        first.MissingTarget = false;
        await using (var db = factory.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [agent].[FailPackageInvalidation] ON [agent].[Packages] AFTER UPDATE AS THROW 51000, 'Injected failure.', 1;", token);
        var next = new TestRepository(key, 2, Catalogue("aa", false), metadataVersion: 2);
        var injected = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, next), TimeProvider.System, sourceVersion, cancellationToken: token));
        Assert.AreEqual(51000, injected.Number);
        Assert.Contains("Injected failure.", injected.Message);
        await using (var lease = await store.AcquireAsync(token))
        {
            using var timestamp = JsonDocument.Parse(lease.LoadMetadata("timestamp")!);
            Assert.AreEqual(1, timestamp.RootElement.GetProperty("signed").GetProperty("version").GetInt32());
        }
        await using (var db = factory.CreateDbContext())
        {
            Assert.IsTrue((await db.Packages.SingleAsync(token)).Listed);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [agent].[FailPackageInvalidation]", token);
        }
        next.MissingTarget = true;
        await Assert.ThrowsAsync<Tuf.TufException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, next), TimeProvider.System, sourceVersion, cancellationToken: token));
        await using (var db = factory.CreateDbContext()) Assert.IsFalse((await db.Packages.SingleAsync(token)).Listed);
        next.MissingTarget = false;
        await store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, next), TimeProvider.System, sourceVersion, cancellationToken: token);
        await using (var db = factory.CreateDbContext()) Assert.IsTrue((await db.Packages.SingleAsync(token)).Listed);
    });

    [TestMethod]
    public Task Catalogue_QueryPaginatesAndPreservesStatus() => WithDatabase(async (factory, token) =>
    {
        await using (var db = factory.CreateDbContext())
        {
            for (var i = 0; i < 51; i++)
                db.Packages.Add(new AgentPackage
                {
                    Id = Guid.CreateVersion7(), Version = $"1.0.{i}", RuntimeIdentifier = "win-x64",
                    Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Size = 123, Listed = i != 50, Revoked = i == 50,
                    VerifiedUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
                });
            await db.SaveChangesAsync(token);
        }
        var query = new AgentPackageQuery(factory);
        var first = await query.GetAsync(1, token);
        var second = await query.GetAsync(2, token);
        Assert.HasCount(50, first.Items);
        Assert.IsTrue(first.HasMore);
        Assert.HasCount(1, second.Items);
        Assert.IsFalse(second.HasMore);
        var revoked = first.Items.Concat(second.Items).Single(x => x.Version == "1.0.50");
        Assert.IsFalse(revoked.Listed);
        Assert.IsTrue(revoked.Revoked);
        Assert.IsLessThan(DateTimeOffset.UtcNow, revoked.VerifiedUntil);
        Assert.IsEmpty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
    });

    [TestMethod]
    public Task TrustState_RetainsRotatedRootsWhenLaterTargetFetchFails() => WithDatabase(async (factory, token) =>
    {
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = new TestRepository(firstKey);
        var root1 = original.Root(1, firstKey);
        var root2 = original.Root(2, secondKey, firstKey);
        var rotated = new TestRepository(secondKey) { RotatedRoot = root2, MissingTarget = true };
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        var sourceVersion = await SourceVersion(factory, token);
        await store.ProvisionAsync(root1, token);
        var uri = new Uri("https://updates.example.test/");
        await Assert.ThrowsAsync<Tuf.TufException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, rotated), TimeProvider.System, sourceVersion, cancellationToken: token));
        await using var db = factory.CreateDbContext();
        var roots = await db.UpdateRoots.OrderBy(x => x.Version).ToArrayAsync(token);
        Assert.HasCount(2, roots);
        CollectionAssert.AreEqual(root1, roots[0].Metadata);
        CollectionAssert.AreEqual(root2, roots[1].Metadata);
        CollectionAssert.AreEqual(SHA256.HashData(root2), roots[1].Digest);
        CollectionAssert.AreEqual(root1, await store.ReadMetadataAsync("root", 1, token));
        CollectionAssert.AreEqual(root2, await store.ReadMetadataAsync("root", 2, token));
        Assert.IsNull(await store.ReadMetadataAsync("root", 3, token));
        db.UpdateRoots.Remove(roots[1]);
        await db.SaveChangesAsync(token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadMetadataAsync("root", 2, token));
    });

    [TestMethod]
    public Task TrustState_RootHistoryFailureDoesNotAdvanceTrust() => WithDatabase(async (factory, token) =>
    {
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = new TestRepository(firstKey);
        var root1 = original.Root(1, firstKey);
        var root2 = original.Root(2, secondKey, firstKey);
        var rotated = new TestRepository(secondKey) { RotatedRoot = root2 };
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        var sourceVersion = await SourceVersion(factory, token);
        await store.ProvisionAsync(root1, token);
        await using (var db = factory.CreateDbContext())
        {
            db.UpdateRoots.Add(new UpdateRoot { Version = 2, Metadata = root2, Digest = SHA256.HashData(root2) });
            await db.SaveChangesAsync(token);
        }
        var uri = new Uri("https://updates.example.test/");
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, rotated), TimeProvider.System, sourceVersion, cancellationToken: token));
        Assert.IsInstanceOfType<Microsoft.Data.SqlClient.SqlException>(failure.InnerException);
        Assert.IsTrue(((Microsoft.Data.SqlClient.SqlException)failure.InnerException).Number is 2601 or 2627);
        await using (var lease = await store.AcquireAsync(token)) CollectionAssert.AreEqual(root1, lease.LoadMetadata("root"));
        await using (var db = factory.CreateDbContext()) Assert.AreEqual(2, await db.UpdateRoots.CountAsync(token));
    });

    private static byte[] Catalogue(string hashByte, bool revoked) => Encoding.UTF8.GetBytes(
        $$"""{"schema":1,"packages":[{"version":"1.2.3","rid":"win-x64","size":123,"sha256":"{{string.Concat(Enumerable.Repeat(hashByte, 32))}}","manifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","revoked":{{revoked.ToString().ToLowerInvariant()}}}]}""");

    private Task WithDatabase(Func<IDbContextFactory<AgentManagementDbContext>, CancellationToken, Task> test) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await using (var resources = new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options))
                await resources.Database.MigrateAsync(token);
            var factory = new Factory(() => new AgentManagementDbContext(new DbContextOptionsBuilder<AgentManagementDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options));
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync(token);
                context.PackageSources.Add(new AgentPackageSource { Id = Guid.CreateVersion7(), BaseUri = "https://updates.example.test/" });
                await context.SaveChangesAsync(token);
            }
            await test(factory, token);
        }, testContext.CancellationToken);

    private static byte[] Envelope(string type, int version) => Encoding.UTF8.GetBytes(
        $$"""{"signed":{"_type":"{{type}}","version":{{version}}},"signatures":[{}]}""");
    private sealed class Factory(Func<AgentManagementDbContext> create) : IDbContextFactory<AgentManagementDbContext>
    {
        public AgentManagementDbContext CreateDbContext() => create();
    }

    private static async Task<byte[]> SourceVersion(IDbContextFactory<AgentManagementDbContext> factory, CancellationToken token)
    {
        await using var context = await factory.CreateDbContextAsync(token);
        return await context.PackageSources.Select(x => x.Version).SingleAsync(token);
    }
}

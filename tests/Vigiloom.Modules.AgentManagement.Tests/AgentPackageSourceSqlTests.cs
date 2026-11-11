using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.UpdateSecurity;
using Vigiloom.UpdateSecurity.Tests;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentPackageSourceSqlTests(TestContext testContext)
{
    [TestMethod]
    public Task PackageCache_VerifiesSignedCatalogueManifestAndArchive() => WithDatabase(true, async (factory, token) =>
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var archive = "signed package bytes"u8.ToArray();
        var archiveHash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var manifest = Encoding.UTF8.GetBytes($$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{archiveHash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""");
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
        var catalogue = Encoding.UTF8.GetBytes($$"""{"schema":1,"packages":[{"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{archiveHash}}","manifestSha256":"{{manifestHash}}","revoked":false}]}""");
        var repository = new TestRepository(signer, catalogue: catalogue, additionalTargets: new Dictionary<string, byte[]> { [manifestHash + ".json"] = manifest });
        var settings = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), TimeProvider.System);
        var source = await settings.UpdateAsync(new("https://packages.example.test/", PackageCredentialAction.Replace, "test-token", ""), Guid.CreateVersion7(), token);
        Assert.IsNotNull(source);
        var trust = new UpdateTrustStore(factory, new OperationAuditWriter());
        await trust.ProvisionAsync(repository.Root(1, signer), token);
        var uri = new Uri("https://packages.example.test/");
        await trust.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, repository), TimeProvider.System, Convert.FromBase64String(source.Version), cancellationToken: token);
        Guid packageId;
        await using (var db = factory.CreateDbContext()) packageId = (await db.Packages.SingleAsync(token)).Id;

        var path = Path.Combine(Path.GetTempPath(), $"vigiloom-library-test-{Guid.NewGuid():N}");
        try
        {
            using var http = new HttpClient(new RepositoryHandler(repository, archiveHash, archive));
            var transport = new PackageLibraryTransport(path, 1024, TimeProvider.System, http);
            var library = new PackageLibrary(factory, settings, trust, transport, TimeProvider.System);
            Assert.IsTrue(await library.CacheAsync(packageId, token));
            CollectionAssert.AreEqual(archive, await File.ReadAllBytesAsync(Path.Combine(path, archiveHash + ".pkg"), token));
        }
        finally
        {
            var full = Path.GetFullPath(path);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(full), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(full).StartsWith("vigiloom-library-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected package directory.");
            if (Directory.Exists(full))
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Refusing to remove a linked directory.");
                Directory.Delete(full, recursive: true);
            }
        }
    });

    [TestMethod]
    public Task PackageCache_RechecksRevocationAndSourceAfterTransfer() => WithDatabase(true, async (factory, token) =>
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), TimeProvider.System);
        var actor = Guid.CreateVersion7();
        var source = await settings.UpdateAsync(new("https://packages.example.test/", PackageCredentialAction.Keep, null, ""), actor, token);
        Assert.IsNotNull(source);
        var trust = new UpdateTrustStore(factory, new OperationAuditWriter());
        await trust.ProvisionAsync(new TestRepository(signer).Root(1, signer), token);
        var hash = new string('a', 64);
        var manifest = Encoding.UTF8.GetBytes($$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":1,"sha256":"{{hash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""");
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
        var id = Guid.CreateVersion7();
        await using (var db = factory.CreateDbContext())
        {
            db.Packages.Add(new AgentPackage { Id = id, Version = "1.0.0", RuntimeIdentifier = "win-x64", Size = 1,
                Sha256 = hash, ManifestSha256 = manifestHash, Listed = true, VerifiedUntil = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync(token);
        }
        var transport = new FixturePackageTransport(manifest);
        var library = new PackageLibrary(factory, settings, trust, transport, TimeProvider.System);
        Assert.IsTrue(await library.CacheAsync(id, token));
        Assert.AreEqual(1, transport.Cached);
        transport.DuringCache = async cancellation =>
        {
            await using var db = factory.CreateDbContext();
            await db.Packages.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revoked, true), cancellation);
        };
        Assert.IsFalse(await library.CacheAsync(id, token));
        Assert.AreEqual(2, transport.Cached);
        Assert.IsFalse(await library.CacheAsync(id, token));
        await using (var db = factory.CreateDbContext())
            await db.Packages.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revoked, false), token);
        transport.DuringCache = async cancellation =>
            Assert.IsNotNull(await settings.UpdateAsync(new("https://other.example.test/", PackageCredentialAction.Keep, null, source.Version), actor, cancellation));
        Assert.IsFalse(await library.CacheAsync(id, token));
        Assert.AreEqual(3, transport.Cached);
        Assert.IsFalse(await library.CacheAsync(id, token));
        Assert.AreEqual(3, transport.Cached);
    });

    [TestMethod]
    public Task Bootstrap_RequiresFingerprintAndAuditsExactlyOnce() => WithDatabase(true, async (factory, token) =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new TestRepository(key).Root(1, key);
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        var actor = Guid.CreateVersion7();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ProvisionForAdministratorAsync(root, new string('0', 64), actor, token));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(root));
        await store.ProvisionForAdministratorAsync(root, fingerprint, actor, token);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ProvisionForAdministratorAsync(root, fingerprint, actor, token));
        await using var db = factory.CreateDbContext();
        Assert.AreEqual(1, await db.UpdateTrustStates.CountAsync(token));
        var events = await db.Database.SqlQuery<string>($"SELECT [Details] AS [Value] FROM [operations].[AuditEvents] WHERE [Kind] = 20").ToArrayAsync(token);
        Assert.HasCount(1, events);
        Assert.Contains(fingerprint, events[0]);
        var operations = new OperationsFactory(db.Database.GetConnectionString()!);
        var audit = new OperationAuditQueryService(operations);
        var range = new Vigiloom.Contracts.Operations.OperationAuditQuery(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var packageEvents = await audit.QueryAgentPackagesAsync(range, token);
        Assert.HasCount(1, packageEvents.Items);
        Assert.AreEqual(fingerprint, packageEvents.Items[0].AgentPackageTrust?.Sha256);
        Assert.HasCount(0, (await audit.QueryAsync(range, token)).Items);
    });

    [TestMethod]
    public Task Bootstrap_RollsBackWhenAuditFails() => WithDatabase(false, async (factory, token) =>
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new TestRepository(key).Root(1, key);
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => store.ProvisionForAdministratorAsync(
            root, Convert.ToHexStringLower(SHA256.HashData(root)), Guid.CreateVersion7(), token));
        await using var db = factory.CreateDbContext();
        Assert.AreEqual(0, await db.UpdateTrustStates.CountAsync(token));
        Assert.AreEqual(0, await db.UpdateRoots.CountAsync(token));
    });

    [TestMethod]
    public Task SourceCredential_SharedKeyRingAllowsWorkerToReadWebSettings() => WithDatabase(true, async (factory, token) =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"vigiloom-dp-test-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(path);
        try
        {
            static IDataProtectionProvider Provider(DirectoryInfo keys) => DataProtectionProvider.Create(keys,
                options => options.SetApplicationName("Vigiloom-test"));
            var web = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), Provider(directory), TimeProvider.System);
            await web.UpdateAsync(new("https://packages.example.test/", PackageCredentialAction.Replace, "test-token", ""), Guid.CreateVersion7(), token);

            // A separate provider represents Worker using the same persisted key ring. The
            // missing trust root is reached only after the stored credential is decrypted.
            var worker = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), Provider(directory), TimeProvider.System);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RefreshScheduledAsync(new UpdateTrustStore(factory, new OperationAuditWriter()), token));
            Assert.Contains("not been provisioned", exception.Message);
        }
        finally
        {
            var full = Path.GetFullPath(path);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(full), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(full).StartsWith("vigiloom-dp-test-", StringComparison.Ordinal) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to remove an unexpected key directory.");
            Directory.Delete(full, recursive: true);
        }
    });

    [TestMethod]
    public Task SourceSettings_EncryptCredentialAuditAndInvalidateOldCatalogue() => WithDatabase(true, async (factory, token) =>
    {
        var settings = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), TimeProvider.System);
        Assert.IsNull(await settings.GetAsync(token));
        var actor = Guid.CreateVersion7();
        var created = await settings.UpdateAsync(new("https://packages.example.test/", PackageCredentialAction.Replace, "sensitive-token", ""), actor, token);
        Assert.IsNotNull(created);
        Assert.IsTrue(created.HasCredential);
        await using (var db = factory.CreateDbContext())
        {
            var source = await db.PackageSources.SingleAsync(token);
            Assert.AreNotEqual("sensitive-token", source.ProtectedCredential);
            Assert.DoesNotContain("sensitive-token", source.ProtectedCredential!);
            db.Packages.Add(new AgentPackage { Id = Guid.CreateVersion7(), Version = "1.0.0", RuntimeIdentifier = "win-x64",
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Size = 1, Listed = true, VerifiedUntil = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync(token);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(new("https://other.example.test/", PackageCredentialAction.Keep, null, created.Version), actor, token));
        var changed = await settings.UpdateAsync(new("https://other.example.test/", PackageCredentialAction.Clear, null, created.Version), actor, token);
        Assert.IsNotNull(changed);
        Assert.IsFalse(changed.HasCredential);
        Assert.IsNull(await settings.UpdateAsync(new("https://stale.example.test/", PackageCredentialAction.Keep, null, created.Version), actor, token));
        await using (var db = factory.CreateDbContext()) Assert.IsFalse((await db.Packages.SingleAsync(token)).Listed);
        await using (var db = factory.CreateDbContext())
        {
            var details = await db.Database.SqlQuery<string>($"SELECT [Details] AS [Value] FROM [operations].[AuditEvents]").ToArrayAsync(token);
            Assert.HasCount(2, details);
            Assert.IsFalse(details.Any(x => x.Contains("sensitive-token", StringComparison.Ordinal) || x.Contains("example.test", StringComparison.Ordinal)));
        }
    });

    [TestMethod]
    public Task SourceSettings_RollsBackIfAuditFails() => WithDatabase(false, async (factory, token) =>
    {
        var settings = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), TimeProvider.System);
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => settings.UpdateAsync(
            new("https://packages.example.test/", PackageCredentialAction.Replace, "sensitive-token", ""), Guid.CreateVersion7(), token));
        Assert.IsNull(await settings.GetAsync(token));
    });

    [TestMethod]
    public Task SourceSettings_ChangingSourceDuringRefreshDoesNotPublishOldSource() => WithDatabase(true, async (factory, token) =>
    {
        var settings = new AgentPackageSourceSettings(factory, new OperationAuditWriter(), new EphemeralDataProtectionProvider(), TimeProvider.System);
        var actor = Guid.CreateVersion7();
        var source = await settings.UpdateAsync(new("https://packages.example.test/", PackageCredentialAction.Keep, null, ""), actor, token);
        Assert.IsNotNull(source);
        var sourceVersion = Convert.FromBase64String(source.Version);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = Encoding.UTF8.GetBytes("""{"schema":1,"packages":[{"version":"1.0.0","rid":"win-x64","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","manifestSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","revoked":false}]}""");
        var first = new TestRepository(signer, catalogue: json);
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        await store.ProvisionAsync(first.Root(1, signer), token);
        var uri = new Uri("https://packages.example.test/");
        await store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, first), TimeProvider.System, sourceVersion, actor, token);
        await using (var auditDb = factory.CreateDbContext())
        {
            var recorded = await auditDb.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [operations].[AuditEvents] WHERE [Kind] = 19").SingleAsync(token);
            Assert.AreEqual(1, recorded);
        }
        var next = new TestRepository(signer, 2, json, metadataVersion: 2);
        next.BeforeTarget = async cancellation =>
            Assert.IsNotNull(await settings.UpdateAsync(new("https://other.example.test/", PackageCredentialAction.Keep, null, source.Version), actor, cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RefreshCatalogueAsync(new TrustedCatalogueReader(uri, uri, next), TimeProvider.System, sourceVersion, cancellationToken: token));
        await using var db = factory.CreateDbContext();
        Assert.IsFalse((await db.Packages.SingleAsync(token)).Listed);
    });

    [TestMethod]
    public Task ManualRefresh_DoesNotPublishWhenAuditWriteFails() => WithDatabase(false, async (factory, token) =>
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = Encoding.UTF8.GetBytes("""{"schema":1,"packages":[{"version":"1.0.0","rid":"win-x64","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","manifestSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","revoked":false}]}""");
        var repository = new TestRepository(signer, catalogue: json);
        byte[] sourceVersion;
        await using (var db = factory.CreateDbContext())
        {
            var source = new AgentPackageSource { Id = Guid.CreateVersion7(), BaseUri = "https://packages.example.test/" };
            db.PackageSources.Add(source);
            await db.SaveChangesAsync(token);
            sourceVersion = source.Version;
        }
        var store = new UpdateTrustStore(factory, new OperationAuditWriter());
        await store.ProvisionAsync(repository.Root(1, signer), token);
        var uri = new Uri("https://packages.example.test/");
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => store.RefreshCatalogueAsync(
            new TrustedCatalogueReader(uri, uri, repository), TimeProvider.System, sourceVersion, Guid.CreateVersion7(), token));
        await using var check = factory.CreateDbContext();
        Assert.AreEqual(0, await check.Packages.CountAsync(token));
    });

    private Task WithDatabase(bool audit, Func<IDbContextFactory<AgentManagementDbContext>, CancellationToken, Task> test) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await using (var resources = new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options))
                await resources.Database.MigrateAsync(token);
            var factory = new Factory(() => new AgentManagementDbContext(new DbContextOptionsBuilder<AgentManagementDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options));
            await using (var context = factory.CreateDbContext()) await context.Database.MigrateAsync(token);
            if (audit)
            {
                await using var operations = new OperationsDbContext(new DbContextOptionsBuilder<OperationsDbContext>()
                    .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options);
                await operations.Database.MigrateAsync(token);
            }
            await test(factory, token);
        }, testContext.CancellationToken);

    private sealed class Factory(Func<AgentManagementDbContext> create) : IDbContextFactory<AgentManagementDbContext>
    {
        public AgentManagementDbContext CreateDbContext() => create();
    }

    private sealed class OperationsFactory(string connectionString) : IDbContextFactory<OperationsDbContext>
    {
        public OperationsDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options);
    }

    private sealed class FixturePackageTransport(byte[] manifest) : IPackageLibraryTransport
    {
        public int Cached { get; private set; }
        public Func<CancellationToken, Task>? DuringCache { get; set; }
        public Task<VerifiedManifest> ReadManifestAsync(Uri origin, string? credential, ITrustMetadataLease lease,
            string manifestSha256, CancellationToken cancellationToken) =>
            Task.FromResult(new VerifiedManifest(manifest, DateTimeOffset.UtcNow.AddHours(1)));
        public async Task CacheAsync(Uri origin, string? credential, PackageManifest package, CancellationToken cancellationToken)
        {
            Cached++;
            if (DuringCache is not null) await DuringCache(cancellationToken);
        }
    }

    private sealed class RepositoryHandler(TestRepository repository, string archiveHash, byte[] archive) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-token", request.Headers.Authorization?.Parameter);
            var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            byte[]? bytes = null;
            if (parts is ["packages", var hash] && hash == archiveHash) bytes = archive;
            else if (parts is ["targets", var target]) bytes = await repository.FetchTargetAsync(target, cancellationToken);
            else if (parts is ["metadata", var name])
            {
                var fields = name.Split('.');
                var version = fields.Length == 3 && int.TryParse(fields[0], out var value) ? value : (int?)null;
                var role = fields[version is null ? 0 : 1];
                bytes = await repository.FetchMetadataAsync(role, version, cancellationToken);
            }
            return bytes is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}

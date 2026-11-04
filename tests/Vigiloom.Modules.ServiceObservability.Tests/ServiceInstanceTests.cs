using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ServiceInstanceTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Resolver_PreservesExactIdentityAndHonorsAuthorizationAndRetirement(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var other = new ServiceSource("other", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", now);
            var service = new MonitoredService(candidate, now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(source, other);
                db.Services.Add(service);
                await db.SaveChangesAsync(token);
            }

            async Task<InstanceResolution> Resolve(Guid sourceId, string? externalId, bool commit = true)
            {
                await using var db = new ServiceObservabilityDbContext(options);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var result = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
                    sourceId, "orders", "prod", externalId, token);
                if (commit) await tx.CommitAsync(token);
                return result;
            }

            Assert.AreEqual(InstanceResolutionStatus.Unauthorized, (await Resolve(other.Id, "node")).Status);
            Assert.AreEqual(InstanceResolutionStatus.Unidentified, (await Resolve(source.Id, null)).Status);
            var first = await Resolve(source.Id, "node");
            Assert.AreEqual(InstanceResolutionStatus.Resolved, first.Status);
            Assert.AreEqual(7, first.InstanceId!.Value.Version);
            Assert.AreEqual(first.InstanceId, (await Resolve(source.Id, "node")).InstanceId);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                db.Services.Add(new MonitoredService(new ServiceCandidate(other.Id, "orders", "prod", "Orders", now), now));
                await db.SaveChangesAsync(token);
                await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Instances] SET [SourceScopeId] = {other.Id} WHERE [Id] = {first.InstanceId}", token));
            }
            var otherInstance = await Resolve(other.Id, "node");
            Assert.AreEqual(InstanceResolutionStatus.Resolved, otherInstance.Status);
            Assert.AreNotEqual(first.InstanceId, otherInstance.InstanceId);
            var different = await Resolve(source.Id, "Node");
            var trailingSpace = await Resolve(source.Id, "node ");
            var unicode = await Resolve(source.Id, "節點😀");
            var maximum = await Resolve(source.Id, new string('a', 256));
            Assert.AreNotEqual(first.InstanceId, different.InstanceId);
            Assert.AreNotEqual(first.InstanceId, trailingSpace.InstanceId);
            Assert.AreNotEqual(first.InstanceId, unicode.InstanceId);
            Assert.AreEqual(maximum.InstanceId, (await Resolve(source.Id, new string('a', 256))).InstanceId);
            await Assert.ThrowsAsync<ArgumentException>(() => Resolve(source.Id, new string('a', 257)));
            await Assert.ThrowsAsync<ArgumentException>(() => Resolve(source.Id, "\ud800"));
            var rolledBack = await Resolve(source.Id, "rollback", false);
            Assert.AreNotEqual(rolledBack.InstanceId, (await Resolve(source.Id, "rollback")).InstanceId);

            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Instances] SET [RetiredAt] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE [Id] = {first.InstanceId}", token);
                Assert.AreEqual("node ", await db.Instances.Where(x => x.Id == trailingSpace.InstanceId).Select(x => x.ExternalInstanceId).SingleAsync(token));
            }
            Assert.AreEqual(InstanceResolutionStatus.Retired, (await Resolve(source.Id, "node")).Status);
            await using (var db = new ServiceObservabilityDbContext(options))
                Assert.AreEqual(7, await db.Instances.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Resolver_ConcurrentFirstReportsCreateOneInstance() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source);
                db.Services.Add(service);
                await db.SaveChangesAsync(token);
            }
            async Task<InstanceResolution> Resolve()
            {
                await using var db = new ServiceObservabilityDbContext(options);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var result = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", "node", token);
                await tx.CommitAsync(token);
                return result;
            }
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Resolve()));
            Assert.IsTrue(results.All(x => x.Status == InstanceResolutionStatus.Resolved));
            Assert.HasCount(1, results.Select(x => x.InstanceId).Distinct().ToArray());
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await check.Instances.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Resolver_ConcurrentAdmissionRespectsCapacityAndStillResolvesExisting() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source);
                db.Services.Add(service);
                await db.SaveChangesAsync(token);
                await db.Database.OpenConnectionAsync(token);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                // The fixture builds only locally generated GUID and integer literals.
                var values = Enumerable.Range(0, 999).Select(i => $"('{Guid.CreateVersion7():D}', '{service.Id:D}', '{source.Id:D}', N'node-{i}', TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))");
                command.CommandText = "INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES " + string.Join(',', values);
                await command.ExecuteNonQueryAsync(token);
            }
            async Task<InstanceResolution> Resolve(string id)
            {
                await using var db = new ServiceObservabilityDbContext(options);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var result = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", id, token);
                await tx.CommitAsync(token);
                return result;
            }
            var results = await Task.WhenAll(Resolve("last-a"), Resolve("last-b"));
            Assert.AreEqual(1, results.Count(x => x.Status == InstanceResolutionStatus.Resolved));
            Assert.AreEqual(1, results.Count(x => x.Status == InstanceResolutionStatus.CapacityReached));
            Assert.AreEqual(InstanceResolutionStatus.Resolved, (await Resolve("node-0")).Status);
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1000, await check.Instances.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Resolver_ConcurrentDifferentServicesShareTheGlobalLimit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var first = new MonitoredService(new ServiceCandidate(source.Id, "first", "prod", "First", now), now);
            var second = new MonitoredService(new ServiceCandidate(source.Id, "second", "prod", "Second", now), now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source);
                db.Services.AddRange(first, second);
                await db.SaveChangesAsync(token);
            }
            async Task<InstanceResolution> Resolve(string key)
            {
                await using var db = new ServiceObservabilityDbContext(options);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var result = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
                    source.Id, key, "prod", "node", token, new InstanceAdmissionLimits(10, 1));
                await tx.CommitAsync(token);
                return result;
            }
            var results = await Task.WhenAll(Resolve("first"), Resolve("second"));
            Assert.AreEqual(1, results.Count(x => x.Status == InstanceResolutionStatus.Resolved));
            Assert.AreEqual(1, results.Count(x => x.Status == InstanceResolutionStatus.CapacityReached));
            await using var check = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(1, await check.Instances.CountAsync(token));
        }, context.CancellationToken);
}

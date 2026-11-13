using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class MetricRollupInstanceMigrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task MigratesOriginalIdentityWithoutGuessingInstance() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("monitor", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "test", "prod", "Test", now), now.AddDays(-1));
            var other = new MonitoredService(new ServiceCandidate(source.Id, "other", "prod", "Other", now), now.AddDays(-1));
            var instance = Guid.Parse("00112233-4455-7677-8899-aabbccddeeff");
            var start = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 300 * 300);
            var window = new MetricRollupWindow { ServiceId = service.Id, WindowStart = start, InputRevision = 1 };
            await using var db = new ServiceObservabilityDbContext(options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261110195800_MetricRollups", token);
            // The current EF model contains columns that do not exist at this migration boundary.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Sources] ([Id],[Name],[Verifier],[CreatedAt],[UpdatedAt]) VALUES ({source.Id},{source.Name},{source.Verifier},{now},{now})", token);
            foreach (var item in new[] { service, other })
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Services] ([Id],[SourceId],[CandidateId],[ServiceKey],[ServiceNamespace],[Environment],[DisplayName],[ApprovedAt],[CreatedAt],[UpdatedAt],[IsPublic]) VALUES ({item.Id},{item.SourceId},{item.CandidateId},{item.ServiceKey},{item.ServiceNamespace},{item.Environment},{item.DisplayName},{item.ApprovedAt},{item.CreatedAt},{item.UpdatedAt},{false})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[MetricRollupWindows] ([ServiceId],[WindowStart],[InputRevision],[IsComplete],[RawPruned]) VALUES ({service.Id},{start},{1L},{false},{false})", token);
            window.RowId = await db.Database.SqlQuery<long>($"SELECT [RowId] AS [Value] FROM [service].[MetricRollupWindows] WHERE [ServiceId] = {service.Id} AND [WindowStart] = {start}").SingleAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instance},{service.Id},{source.Id},{"node-a"},{now},{now})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Instances] SET [RetiredAt] = {now} WHERE [Id] = {instance}", token);
            var resource = new Resource { Attributes = { new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } } } };
            var metric = new Metric { Name = "load", Gauge = new Gauge() };
            Assert.IsTrue(MetricSeriesIdentity.TryCreate(source.Id, service.Id, instance, resource, null, "", "", metric, [], out var identified));
            Assert.IsTrue(MetricSeriesIdentity.TryCreate(source.Id, service.Id, null, resource, null, "", "", metric, [], out var unidentified));
            Assert.IsNotNull(identified); Assert.IsNotNull(unidentified);
            async Task Insert(byte[] hash, byte[] identity, int ordinal) => await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [service].[MetricRollupSeries] ([WindowRowId],[IdentityHash],[IdentityBytes],[CollisionOrdinal],[Mode],[QualityFlags],[RequiresRaw],[ResultVersion],[InputCount],[InputBytes],[ResultPayload])
                VALUES ({window.RowId},{hash},{identity},{ordinal},{1},{0L},{false},{1},{1},{1L},{new byte[] { 1 }})
                """, token);
            await Insert(identified.Fingerprint.ToByteArray(), identified.Encoded.ToByteArray(), 0);
            await Insert(unidentified.Fingerprint.ToByteArray(), unidentified.Encoded.ToByteArray(), 0);
            var applied = (await db.Database.GetAppliedMigrationsAsync(token)).ToArray();
            var downFailure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(applied[^2], token));
            Assert.AreEqual(51000, downFailure.Number);
            var original = identified.Encoded.ToByteArray();
            var wrongSource = (byte[])original.Clone(); wrongSource[1] ^= 1;
            var wrongService = (byte[])original.Clone(); wrongService[17] ^= 1;
            var wrongVersion = (byte[])original.Clone(); wrongVersion[0] = 2;
            var wrongPresence = (byte[])original.Clone(); wrongPresence[33] = 2;
            var badInputs = new[] { new byte[] { 1 }, wrongSource, wrongService, wrongVersion, wrongPresence,
                original[..49], original[..67], unidentified.Encoded.ToByteArray()[..51] };
            foreach (var bad in badInputs)
            {
                var badHash = SHA256.HashData(bad);
                await Insert(badHash, bad, 0);
                var failure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(null, token));
                Assert.AreEqual(51006, failure.Number);
                Assert.AreEqual(3, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [service].[MetricRollupSeries]").SingleAsync(token));
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [service].[MetricRollupSeries] WHERE [IdentityHash] = {badHash}", token);
            }
            var mismatchedHash = identified.Fingerprint.ToByteArray(); mismatchedHash[0] ^= 1;
            await Insert(mismatchedHash, original, 0);
            var hashFailure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(null, token));
            Assert.AreEqual(51006, hashFailure.Number);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [service].[MetricRollupSeries] WHERE [IdentityHash] = {mismatchedHash}", token);
            await migrator.MigrateAsync(null, token);
            db.ChangeTracker.Clear();
            var results = await db.MetricRollupSeries.OrderBy(x => x.RowId).ToArrayAsync(token);
            Assert.HasCount(2, results);
            Assert.AreEqual(service.Id, results[0].ServiceId);
            Assert.AreEqual(instance, results[0].InstanceId);
            Assert.AreEqual(service.Id, results[1].ServiceId);
            Assert.IsNull(results[1].InstanceId);

            var otherWindow = new MetricRollupWindow { ServiceId = other.Id, WindowStart = start, InputRevision = 1 };
            db.MetricRollupWindows.Add(otherWindow); await db.SaveChangesAsync(token);
            db.MetricRollupSeries.Add(new MetricRollupSeries { WindowRowId = otherWindow.RowId, ServiceId = other.Id, InstanceId = instance,
                IdentityHash = new byte[32], IdentityBytes = [1], CollisionOrdinal = 0, Mode = MetricRollupMode.GaugeSnapshot,
                ResultVersion = 1, InputCount = 1, ResultPayload = [1] });
            var foreignKey = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            var sql = Assert.IsInstanceOfType<SqlException>(foreignKey.InnerException);
            Assert.AreEqual(547, sql.Number);
            Assert.Contains("FK_MetricRollupSeries_Instances_InstanceId_ServiceId", sql.Message);
        }, context.CancellationToken);
}

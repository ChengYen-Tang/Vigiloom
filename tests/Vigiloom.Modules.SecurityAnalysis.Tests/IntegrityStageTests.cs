using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntegrityStageTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Manifest_RejectsRegistryNamesThatDifferOnlyByCase()
    {
        var owner = new IntegrityOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        var items = new[]
        {
            new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["ÄPFEL"]), IntegrityItemKind.RegistryValue, new string('a', 64)),
            new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["äpfel"]), IntegrityItemKind.RegistryValue, new string('a', 64))
        };
        var measurement = IntegrityDigest.MeasureChunk(items);
        var now = DateTimeOffset.UtcNow;
        var digest = IntegrityDigest.Combine([measurement.Digest]);
        var scan = new IntegrityScan { AgentId = owner.AgentId, DeviceId = owner.DeviceId, DeviceGeneration = owner.DeviceGeneration,
            ScopeId = Guid.CreateVersion7(), ScopeRevision = 1, SnapshotId = Guid.CreateVersion7(), Generation = 1,
            EnqueuedAt = now, ObservedAt = now, Outcome = IntegrityScanOutcome.Complete, ChunkCount = 1,
            TotalItems = items.Length, TotalBytes = measurement.Bytes, Digest = digest };
        var chunk = new IntegrityChunk(6, Guid.CreateVersion7(), now, scan.ScopeId, 1, scan.SnapshotId, 1, now,
            IntegrityScanOutcome.Complete, false, null, 0, 1, items.Length, measurement.Bytes, digest, items);
        Assert.IsTrue(IntegrityManifest.Verify(scan, [chunk], owner));
        Assert.IsFalse(IntegrityManifest.Verify(scan, [chunk], owner, IntegrityChanges.RegistryKeys));
    }

    [TestMethod]
    public Task ChangeQuery_PaginatesTiedTimesAcrossAgentsAndRetiredScopes() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            var device = Guid.CreateVersion7();
            var scope = Guid.CreateVersion7();
            var agents = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 55; index++)
            {
                var item = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, [$"{index}"]), IntegrityItemKind.File, new string('a', 64));
                db.IntegrityChanges.Add(new IntegrityChangeRecord { AgentId = agents[index % 2], DeviceId = device,
                    ScopeId = scope, ScopeRevision = 1, Generation = 1, ObservedAt = now, RecordedAt = now,
                    Kind = (int)IntegrityChangeType.Added, Key = item.Key, AfterJson = System.Text.Json.JsonSerializer.Serialize(item), CoverageGap = true });
            }
            await db.SaveChangesAsync(token);
            var query = new IntegrityChangeQuery(new Factory(options));
            var from = now.AddMinutes(-1);
            var until = now.AddMinutes(1);
            var first = await query.GetAsync(device, from, until, scope, null, token);
            Assert.HasCount(50, first.Items);
            Assert.IsNotNull(first.NextCursor);
            var second = await query.GetAsync(device, from, until, scope, first.NextCursor, token);
            Assert.HasCount(5, second.Items);
            Assert.IsNull(second.NextCursor);
            Assert.HasCount(55, first.Items.Concat(second.Items).Select(x => x.After!.Key).Distinct().ToArray());
            Assert.HasCount(2, first.Items.Concat(second.Items).Select(x => x.AgentId).Distinct().ToArray());
            Assert.IsEmpty((await query.GetAsync(device, from, until, Guid.CreateVersion7(), null, token)).Items);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => query.GetAsync(device, from, until, scope, "invalid", token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task RegistryDuplicateAcrossChunks_IsRejectedWithoutReplacingBaseline() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            var device = Guid.CreateVersion7();
            var owner = new IntegrityOwner(Guid.CreateVersion7(), device, 1);
            var config = new IntegrityConfiguration(new Factory(options), new Devices(device), TimeProvider.System);
            var input = new IntegrityScopeInput(IntegrityScopeKind.Registry, "HKLM\\SOFTWARE", true,
                ImmutableArray<string>.Empty, 3600, 100, 1048576, 10485760, 16, IntegrityRegistryView.Registry64);
            var created = (await config.CreateAsync(device, input, Guid.CreateVersion7(), token)).Scope!;
            var upper = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["ÄPFEL"]), IntegrityItemKind.RegistryValue, new string('a', 64));
            var lower = upper with { Key = IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["äpfel"]) };
            var first = IntegrityDigest.MeasureChunk([upper]);
            var second = IntegrityDigest.MeasureChunk([lower]);
            var now = DateTimeOffset.UtcNow;
            var writer = new IntegrityStageWriter();
            async Task Stage(IntegrityChunk chunk)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                Assert.AreEqual(IntegrityStageResult.Accepted, await writer.StageAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), owner, chunk, now, token));
                await transaction.CommitAsync(token);
            }
            var baseline = new IntegrityChunk(6, Guid.CreateVersion7(), now, created.Scope.Id, 1, Guid.CreateVersion7(), 1, now,
                IntegrityScanOutcome.Complete, false, null, 0, 1, 1, first.Bytes, IntegrityDigest.Combine([first.Digest]), [upper]);
            await Stage(baseline);
            var snapshot = Guid.CreateVersion7();
            var digest = IntegrityDigest.Combine([first.Digest, second.Digest]);
            var chunk = new IntegrityChunk(6, Guid.CreateVersion7(), now, created.Scope.Id, 1, snapshot, 2, now,
                IntegrityScanOutcome.Complete, false, null, 0, 2, 2, first.Bytes + second.Bytes, digest, [upper]);
            await Stage(chunk);
            await Stage(chunk with { BatchId = Guid.CreateVersion7(), ChunkIndex = 1, Items = [lower] });
            db.ChangeTracker.Clear();
            var state = await db.IntegrityScopeStates.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(1, state.CurrentGeneration);
            Assert.AreEqual(IntegrityCompletion.Rejected, state.LastStatus);
            Assert.AreEqual(baseline.SnapshotId, (await new IntegrityQuery(new Factory(options)).GetAsync(device, owner.AgentId, token)).Scopes[0].SnapshotId);
            Assert.IsEmpty(await db.IntegrityChanges.ToListAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task CompletePartialAndChangedPolicy_ProtectBaselineAndRecordGaps() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            var device = Guid.CreateVersion7();
            var actor = Guid.CreateVersion7();
            var owner = new IntegrityOwner(Guid.CreateVersion7(), device, 1);
            var config = new IntegrityConfiguration(new Factory(options), new Devices(device), TimeProvider.System);
            var input = new IntegrityScopeInput(IntegrityScopeKind.Directory, "/srv/app", true,
                ImmutableArray<string>.Empty, 3600, 1000, 1048576, 10485760, 16);
            var created = (await config.CreateAsync(device, input, actor, token)).Scope!;
            var writer = new IntegrityStageWriter();
            var now = DateTimeOffset.UtcNow;
            var first = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["first"]), IntegrityItemKind.File, new string('a', 64));
            var second = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["second"]), IntegrityItemKind.File, new string('b', 64));

            IntegrityChunk Chunk(long generation, long revision, IntegrityScanOutcome outcome, IntegrityItem[] items, bool gap = false,
                IntegrityPartialReason? reason = null)
            {
                var measurement = IntegrityDigest.MeasureChunk(items);
                return new(6, Guid.CreateVersion7(), now, created.Scope.Id, revision, Guid.CreateVersion7(), generation, now,
                    outcome, gap, reason, 0, 1, items.Length, measurement.Bytes, IntegrityDigest.Combine([measurement.Digest]), items);
            }
            async Task<IntegrityStageResult> Stage(IntegrityOwner who, IntegrityChunk chunk)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var result = await writer.StageAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), who, chunk, now, token);
                if (result is IntegrityStageResult.Accepted or IntegrityStageResult.SnapshotTooLarge) await transaction.CommitAsync(token);
                return result;
            }

            Assert.AreEqual(IntegrityStageResult.Accepted, await Stage(owner, Chunk(1, 1, IntegrityScanOutcome.Complete, [first, second])));
            Assert.AreEqual(0, await db.IntegrityChanges.CountAsync(token));
            var query = new IntegrityQuery(new Factory(options));
            var current = await query.GetAsync(device, owner.AgentId, token);
            Assert.HasCount(1, current.Scopes);
            var baseline = current.Scopes[0];
            Assert.IsNotNull(baseline.SnapshotId);
            Assert.AreEqual(1, baseline.BaselineRevision);
            var baselineChunk = await query.GetChunkAsync(device, owner.AgentId, created.Scope.Id, baseline.SnapshotId.Value, 0, token);
            Assert.IsNotNull(baselineChunk);
            Assert.HasCount(2, baselineChunk.Items);
            Assert.IsNull(await query.GetChunkAsync(Guid.CreateVersion7(), owner.AgentId, created.Scope.Id, baseline.SnapshotId.Value, 0, token));
            Assert.IsNull(await query.GetChunkAsync(device, Guid.CreateVersion7(), created.Scope.Id, baseline.SnapshotId.Value, 0, token));
            Assert.IsNull((await query.GetAsync(device, null, token)).Scopes[0].SnapshotId);
            Assert.AreEqual(IntegrityStageResult.Accepted, await Stage(owner, Chunk(2, 1, IntegrityScanOutcome.Partial, [first], true, IntegrityPartialReason.AccessDenied)));
            db.ChangeTracker.Clear();
            var state = await db.IntegrityScopeStates.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(1, state.CurrentGeneration);
            Assert.AreEqual(IntegrityCompletion.Partial, state.LastStatus);
            Assert.AreEqual(IntegrityPartialReason.AccessDenied, state.LastPartialReason);
            Assert.AreEqual(0, await db.IntegrityChanges.CountAsync(token));
            Assert.AreEqual(IntegrityStageResult.Accepted, await Stage(owner, Chunk(3, 1, IntegrityScanOutcome.Complete, [first], true)));
            db.ChangeTracker.Clear();
            var removed = await db.IntegrityChanges.SingleAsync(token);
            Assert.AreEqual(3, removed.Kind);
            Assert.AreEqual(second.Key, removed.Key);
            Assert.IsTrue(removed.CoverageGap);
            var history = await new IntegrityChangeQuery(new Factory(options)).GetAsync(device, now.AddMinutes(-1), now.AddMinutes(1), null, null, token);
            Assert.HasCount(1, history.Items);
            Assert.AreEqual(IntegrityChangeType.Removed, history.Items[0].Kind);
            Assert.IsTrue(history.Items[0].CoverageGap);
            Assert.IsEmpty((await new IntegrityChangeQuery(new Factory(options)).GetAsync(Guid.CreateVersion7(), now.AddMinutes(-1), now.AddMinutes(1), null, null, token)).Items);
            Assert.IsNull(await query.GetChunkAsync(device, owner.AgentId, created.Scope.Id, baseline.SnapshotId.Value, 0, token));
            Assert.AreEqual(IntegrityStageResult.Expired, await Stage(owner, Chunk(2, 1, IntegrityScanOutcome.Complete, [second])));
            Assert.AreEqual(IntegrityStageResult.Conflict, await Stage(owner with { DeviceId = Guid.CreateVersion7() }, Chunk(4, 1, IntegrityScanOutcome.Complete, [first])));
            var updated = (await config.UpdateAsync(device, created.Scope.Id, input with { Root = "/srv/new" }, created.Version, actor, token)).Scope!;
            Assert.AreEqual(IntegrityStageResult.Conflict, await Stage(owner, Chunk(4, 1, IntegrityScanOutcome.Complete, [])));
            Assert.AreEqual(IntegrityStageResult.Accepted, await Stage(owner, Chunk(4, updated.Scope.Revision, IntegrityScanOutcome.Complete, [])));
            Assert.AreEqual(1, await db.IntegrityChanges.CountAsync(token));
            var firstChunk = IntegrityDigest.MeasureChunk([first]);
            var secondChunk = IntegrityDigest.MeasureChunk([second]);
            var pending = new IntegrityChunk(6, Guid.CreateVersion7(), now, created.Scope.Id, updated.Scope.Revision, Guid.CreateVersion7(), 5,
                now, IntegrityScanOutcome.Complete, false, null, 0, 2, 2, firstChunk.Bytes + secondChunk.Bytes,
                IntegrityDigest.Combine([firstChunk.Digest, secondChunk.Digest]), [first]);
            Assert.AreEqual(IntegrityStageResult.Accepted, await Stage(owner, pending));
            Assert.AreEqual(IntegrityConfigurationStatus.Success,
                (await config.RetireAsync(device, created.Scope.Id, updated.Version, actor, token)).Status);
            var expiry = new IntegrityExpiry(new Factory(options));
            Assert.IsGreaterThan(0, await expiry.RunAsync(token));
            db.ChangeTracker.Clear();
            var retiredState = await db.IntegrityScopeStates.AsNoTracking().SingleAsync(token);
            Assert.IsNull(retiredState.CurrentGeneration);
            Assert.IsNull(retiredState.ActiveGeneration);
            Assert.AreEqual(5, retiredState.ClosedGeneration);
            Assert.AreEqual(IntegrityCompletion.Expired, retiredState.LastStatus);
            Assert.AreEqual(pending.SnapshotId, retiredState.LastSnapshotId);
            Assert.AreEqual(0, await db.IntegrityChunks.CountAsync(token));
            Assert.AreEqual(1, await db.IntegrityChanges.CountAsync(token));
            Assert.IsEmpty((await query.GetAsync(device, owner.AgentId, token)).Scopes);
            Assert.HasCount(1, (await new IntegrityChangeQuery(new Factory(options)).GetAsync(device, now.AddMinutes(-1), now.AddMinutes(1), created.Scope.Id, null, token)).Items);
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

    private sealed class Devices(Guid deviceId) : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid id, CancellationToken token) => Task.FromResult(id == deviceId ? new DeviceAgentState(id, 1, true) : null);
        public Task<bool> IsCurrentAsync(Guid id, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            Task.FromResult(id == deviceId && generation == 1);
    }
}

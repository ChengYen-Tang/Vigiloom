using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class IntegrityQueueTests(TestContext context)
{
    [TestMethod]
    public async Task Cycle_RejectsOldPolicyIfChangedWhileWaitingToEnqueue()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityQueueTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/",
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, "/srv/app", true,
                ImmutableArray<string>.Empty, 60, 100, 1024, 4096, 4);
            var store = new AgentIntegritySettingsStore(vault, new Uri(owner.Server), id);
            var saved = await store.SaveAsync(IntegritySettingsSnapshot.Create([scope], 1), null, DateTimeOffset.UtcNow, token);
            var status = new IntegrityScopeStatus(scope.Id, true, 1, true, 0, null, false, null, null, null, null, null, null);
            using var http = new HttpClient(new StatusHandler(status));
            var cycle = new IntegrityCycle(new IntegrityStatusClient(http, TimeProvider.System), TimeProvider.System);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = cycle.RunAsync(vault, new Uri(owner.Server), owner, saved, scope,
                (_, _) => Task.FromResult(new IntegrityReadResult(IntegrityScanOutcome.Complete, [], true)), token,
                async (enqueue, inner) => { reached.SetResult(); await proceed.Task.WaitAsync(inner); return await enqueue(inner); });
            await reached.Task.WaitAsync(token);
            await store.SaveAsync(IntegritySettingsSnapshot.Create([scope with { Enabled = false, Revision = 2 }], 2),
                saved.SaveToken, DateTimeOffset.UtcNow, token);
            proceed.SetResult();
            Assert.AreEqual(IntegrityCycleKind.PolicyChanged, (await running.WaitAsync(token)).Kind);
            Assert.IsNull(await new DurableBatchQueue(vault, id).ReadOutstandingIntegrityAsync(owner, token));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Queue_AtomicallyFencesPendingGenerationAndPreservesNewFormat()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityQueueTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/",
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            var queue = new DurableBatchQueue(vault, id);
            var now = DateTimeOffset.UtcNow;
            var scope = Guid.CreateVersion7();
            var setting = new IntegrityScope(scope, 1, IntegrityScopeKind.Directory, "/srv/app", true, ImmutableArray<string>.Empty,
                3600, 1000, 1048576, 10485760, 16);
            var store = new AgentIntegritySettingsStore(vault, new Uri("https://example.test/"), id);
            var saved = await store.SaveAsync(IntegritySettingsSnapshot.Create([setting], 1), null, now, token);
            var snapshot = Guid.CreateVersion7();
            var gap = await queue.MarkIntegrityGapAsync(owner, scope, 1, token);
            var batches = IntegrityChunkBuilder.Build(scope, 1, snapshot, 1, now, now, IntegrityScanOutcome.Complete, [], true);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueManyForOwnerAsync(batches, owner, token));
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueIntegrityForOwnerAsync(batches, owner, scope, 1, 1, snapshot, saved.SaveToken, gap, token));
            Assert.AreEqual(EnqueueResult.AlreadyQueued, await queue.EnqueueIntegrityForOwnerAsync(batches, owner, scope, 1, 1, snapshot, saved.SaveToken, gap, token));
            var otherSnapshot = Guid.CreateVersion7();
            var other = IntegrityChunkBuilder.Build(scope, 1, otherSnapshot, 2, now, now, IntegrityScanOutcome.Complete, []);
            Assert.AreEqual(EnqueueResult.IntegrityOutstanding, await queue.EnqueueIntegrityForOwnerAsync(other, owner, scope, 1, 2, otherSnapshot, saved.SaveToken, null, token));
            await store.SaveAsync(IntegritySettingsSnapshot.Create([setting with { Enabled = false, Revision = 2 }], 2), saved.SaveToken, now, token);
            Assert.AreEqual(EnqueueResult.IntegrityPolicyChanged, await queue.EnqueueIntegrityForOwnerAsync(other, owner, scope, 1, 2, otherSnapshot, saved.SaveToken, null, token));
            Assert.IsTrue(await queue.AcknowledgeAsync(batches[0].BatchId, token));
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.AreEqual(6L, (long)(await command.ExecuteScalarAsync(token))!);
            command.CommandText = "SELECT COUNT(*) FROM integrity_outstanding";
            Assert.AreEqual(1L, (long)(await command.ExecuteScalarAsync(token))!);
            var status = new IntegrityScopeStatus(scope, true, 1, true, 1, null, false, 1, IntegrityScanOutcome.Complete,
                IntegrityCompletion.Complete, snapshot, null, false);
            Assert.AreEqual(IntegrityReconcileResult.Completed, await queue.ReconcileIntegrityAsync(owner,
                new(scope, 1, 1, snapshot), new(IntegrityStatusResultKind.Success, status, owner), token));
            Assert.IsNull(await queue.ReadOutstandingIntegrityAsync(owner, token));
            Assert.IsNull(await queue.ReadIntegrityGapAsync(owner, scope, 1, token));
            command.CommandText = "PRAGMA user_version";
            Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync(token))!);
            var enabled = await store.SaveAsync(IntegritySettingsSnapshot.Create([setting with { Enabled = true, Revision = 3 }], 3),
                (await store.ReadAsync(token))!.SaveToken, now, token);
            var retiredSnapshot = Guid.CreateVersion7();
            var retiredBatch = IntegrityChunkBuilder.Build(scope, 3, retiredSnapshot, 2, now, now, IntegrityScanOutcome.Complete, []);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueIntegrityForOwnerAsync(retiredBatch, owner, scope, 3, 2, retiredSnapshot, enabled.SaveToken, null, token));
            var retired = new IntegrityScopeStatus(scope, false, null, null, 1, null, false, 1,
                IntegrityScanOutcome.Complete, IntegrityCompletion.Complete, snapshot, null, false);
            await store.SaveAsync(IntegritySettingsSnapshot.Create([], 4), enabled.SaveToken, now, token);
            using var http = new HttpClient(new StatusHandler(retired));
            var cycle = new IntegrityCycle(new IntegrityStatusClient(http, TimeProvider.System), TimeProvider.System);
            var resumed = await cycle.ResumeOutstandingAsync(vault, new Uri("https://example.test/"), owner, token);
            Assert.AreEqual(IntegrityCycleKind.Rejected, resumed!.Kind);
            Assert.IsNull(await queue.ReadOutstandingIntegrityAsync(owner, token));
            command.CommandText = "PRAGMA user_version";
            Assert.AreEqual(6L, (long)(await command.ExecuteScalarAsync(token))!);
            var activeAgain = await store.SaveAsync(IntegritySettingsSnapshot.Create([setting with { Enabled = true, Revision = 4 }], 5),
                (await store.ReadAsync(token))!.SaveToken, now, token);
            var gapA = await queue.MarkIntegrityGapAsync(owner, scope, 4, token);
            var pendingSnapshot = Guid.CreateVersion7();
            var incomplete = IntegrityChunkBuilder.Build(scope, 4, pendingSnapshot, 3, now, now,
                IntegrityScanOutcome.Partial, [], true, IntegrityPartialReason.AccessDenied);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueIntegrityForOwnerAsync(incomplete, owner, scope, 4, 3, pendingSnapshot, activeAgain.SaveToken, gapA, token));
            var partialStatus = new IntegrityScopeStatus(scope, true, 4, true, 3, null, false, 3,
                IntegrityScanOutcome.Partial, IntegrityCompletion.Partial, pendingSnapshot, IntegrityPartialReason.AccessDenied, true);
            Assert.AreEqual(IntegrityReconcileResult.Completed, await queue.ReconcileIntegrityAsync(owner,
                new(scope, 4, 3, pendingSnapshot), new(IntegrityStatusResultKind.Success, partialStatus, owner), token));
            Assert.AreEqual(gapA, await queue.ReadIntegrityGapAsync(owner, scope, 4, token));
            var completeSnapshot = Guid.CreateVersion7();
            var complete = IntegrityChunkBuilder.Build(scope, 4, completeSnapshot, 4, now, now, IntegrityScanOutcome.Complete, [], true);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueIntegrityForOwnerAsync(complete, owner, scope, 4, 4, completeSnapshot, activeAgain.SaveToken, gapA, token));
            var gapB = await queue.MarkIntegrityGapAsync(owner, scope, 4, token);
            Assert.IsGreaterThan(gapA, gapB);
            var completeStatus = new IntegrityScopeStatus(scope, true, 4, true, 4, null, false, 4,
                IntegrityScanOutcome.Complete, IntegrityCompletion.Complete, completeSnapshot, null, true);
            Assert.AreEqual(IntegrityReconcileResult.Completed, await queue.ReconcileIntegrityAsync(owner,
                new(scope, 4, 4, completeSnapshot), new(IntegrityStatusResultKind.Success, completeStatus, owner), token));
            Assert.AreEqual(gapB, await queue.ReadIntegrityGapAsync(owner, scope, 4, token));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StatusHandler(IntegrityScopeStatus status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(status) });
    }
}

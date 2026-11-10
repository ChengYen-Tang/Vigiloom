using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Microsoft.Data.Sqlite;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class MetricLossLedgerTests(TestContext context)
{
    [TestMethod]
    public async Task SameAgentIdOnDifferentServer_CannotReuseLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomMetricLossTests", Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/", MachineSecret = "secret" }, token);
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var ledger = await MetricLossLedger.StartAsync(vault, owner, token)) await ledger.CompleteAsync(token);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = id, Server = "https://other.test/", MachineSecret = "secret" }, token);
            var other = new AgentSettingsOwner(id, "https://other.test/", vault.StateDirectory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MetricLossLedger.StartAsync(vault, other, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task DisposedOrCompletedWriter_CannotMutateLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomMetricLossTests", Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/", MachineSecret = "secret" }, token);
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            var ledger = await MetricLossLedger.StartAsync(vault, owner, token);
            await ledger.CompleteAsync(token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.RecordAsync(1, 1, 1, token));
            await ledger.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => ledger.RecordAsync(1, 1, 1, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    [DataRow("records = -1")]
    [DataRow("unknown = 0.5")]
    [DataRow("records = 'invalid'")]
    [DataRow("records = X'0102'")]
    public async Task CorruptTotals_AreRejectedWithoutReinitialization(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomMetricLossTests", Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/", MachineSecret = "secret" }, token);
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var ledger = await MetricLossLedger.StartAsync(vault, owner, token)) await ledger.CompleteAsync(token);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "metric-loss.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE loss_state SET " + corruption;
                command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => MetricLossLedger.StartAsync(vault, owner, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task ChangedIdentity_CannotReadOrMutatePreviousAgentsLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomMetricLossTests", Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/", MachineSecret = "secret" }, token);
            var oldOwner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using var ledger = await MetricLossLedger.StartAsync(vault, oldOwner, token);
            await ledger.RecordAsync(1, 1, 2, token);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = Guid.CreateVersion7(), Server = "https://example.test/", MachineSecret = "new" }, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.ReadAsync(token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.RecordAsync(2, 1, 1, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task SettledEvents_AreIdempotentAndIncompleteSessionMarksUnknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomMetricLossTests", Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var identity = vault.Acquire())
                await identity.WriteAsync(new IdentityState { AgentId = id, Server = "https://example.test/", MachineSecret = "secret" }, token);
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            Guid ledgerId;
            await using (var first = await MetricLossLedger.StartAsync(vault, owner, token))
            {
                var initial = await first.ReadAsync(token);
                ledgerId = initial.LedgerId;
                Assert.AreEqual(0L, initial.Records);
                // A newly created database cannot prove whether earlier ledger
                // state existed and was lost before this point.
                Assert.IsTrue(initial.CompletenessUnknown);
                await first.RecordAsync(1, 2, 7, token);
                await first.RecordAsync(1, 2, 7, token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => first.RecordAsync(1, 2, 8, token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => first.RecordAsync(3, 1, 1, token));
                var saved = await first.ReadAsync(token);
                Assert.AreEqual(2L, saved.Batches);
                Assert.AreEqual(7L, saved.Records);
                // Dispose without completion represents a stopped writer whose
                // pending in-memory data can no longer be assessed exactly.
            }
            await using (var second = await MetricLossLedger.StartAsync(vault, owner, token))
            {
                var recovered = await second.ReadAsync(token);
                Assert.AreEqual(ledgerId, recovered.LedgerId);
                Assert.AreEqual(7L, recovered.Records);
                Assert.IsTrue(recovered.CompletenessUnknown);
                await second.RecordAsync(1, 1, 3, token);
                await second.CompleteAsync(token);
            }
            await using (var third = await MetricLossLedger.StartAsync(vault, owner, token))
            {
                var saved = await third.ReadAsync(token);
                Assert.AreEqual(3L, saved.Batches);
                Assert.AreEqual(10L, saved.Records);
                Assert.IsTrue(saved.CompletenessUnknown);
                await third.CompleteAsync(token);
            }
        }
        finally { Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomMetricLossTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

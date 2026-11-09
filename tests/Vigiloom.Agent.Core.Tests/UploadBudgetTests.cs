using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class UploadBudgetTests(TestContext context)
{
    [TestMethod]
    public async Task Reservation_IsSharedAcrossQueuesAndSurvivesRecreation()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomUploadBudgetTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { AgentId = id, Server = owner.Server, MachineSecret = "test" }, token);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var limit = 2L * DurableBatchQueue.MaximumBatchBytes;
            var first = new DurableBatchQueue(vault, id);
            var second = new DurableBatchQueue(vault, id);
            var results = await Task.WhenAll(
                first.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, limit, clock, token),
                second.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, limit, clock, token));
            Assert.IsTrue(results.All(x => x.Allowed));
            var recreated = new DurableBatchQueue(vault, id);
            var denied = await recreated.ReserveUploadAsync(owner, 1, limit, clock, token);
            Assert.IsFalse(denied.Allowed);
            Assert.AreEqual(TimeSpan.FromMinutes(1), denied.RetryAfter);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsTrue((await recreated.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, limit, clock, token)).Allowed);
            var final = await Task.WhenAll(
                first.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, limit, clock, token),
                second.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, limit, clock, token));
            Assert.AreEqual(1, final.Count(x => x.Allowed));
            var wrongOwner = owner with { Server = "https://other.test/" };
            Assert.IsTrue((await recreated.ReserveUploadAsync(wrongOwner, 1, limit, clock, token)).OwnerChanged);
            var resetClock = new ManualClock { Timestamp = 2000 };
            Assert.IsFalse((await recreated.ReserveUploadAsync(owner, 1, limit, resetClock, token)).Allowed);
            resetClock.Timestamp = 1000;
            var reset = await recreated.ReserveUploadAsync(owner, 1, limit, resetClock, token);
            Assert.IsFalse(reset.Allowed);
            Assert.AreEqual(TimeSpan.FromMinutes(1), reset.RetryAfter);
            resetClock.Timestamp = 61000;
            Assert.IsTrue((await recreated.ReserveUploadAsync(owner, 1, limit, resetClock, token)).Allowed);
            var store = new AgentMetricSettingsStore(vault, new Uri(owner.Server), id);
            var low = AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 2000, 2));
            var saved = await store.SaveAsync(low, null, DateTimeOffset.UtcNow, token);
            Assert.IsTrue((await recreated.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, null, resetClock, token)).Allowed);
            Assert.IsFalse((await recreated.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, null, resetClock, token)).Allowed);
            var higher = AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, 512, 2000, 4));
            await store.SaveAsync(higher, saved.SaveToken, DateTimeOffset.UtcNow, token);
            Assert.IsTrue((await recreated.ReserveUploadAsync(owner, DurableBatchQueue.MaximumBatchBytes, null, resetClock, token)).Allowed);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }
}

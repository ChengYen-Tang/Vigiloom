using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class DurableTufCacheTests
{
    [TestMethod]
    public void Load_RequiresExplicitRootAndRejectsCorruptState()
    {
        var lease = new Lease();
        var cache = new DurableTufCache(lease);
        Assert.ThrowsExactly<InvalidDataException>(() => cache.LoadMetadata("root"));
        Assert.IsNull(cache.LoadMetadata("timestamp"));
        lease.Data["root"] = Envelope("root", 1);
        Assert.IsNotNull(cache.LoadMetadata("root"));
        lease.Data["timestamp"] = Encoding.UTF8.GetBytes("not-json");
        Assert.ThrowsExactly<InvalidDataException>(() => cache.LoadMetadata("timestamp"));
    }

    [TestMethod]
    public void Store_PropagatesPersistenceFailureAndRejectsWrongRole()
    {
        var lease = new Lease { FailWrites = true };
        var cache = new DurableTufCache(lease);
        Assert.ThrowsExactly<IOException>(() => cache.StoreMetadata("root", Envelope("root", 2)));
        Assert.IsEmpty(lease.Data);
        lease.FailWrites = false;
        Assert.ThrowsExactly<InvalidDataException>(() => cache.StoreMetadata("timestamp", Envelope("root", 1)));
        Assert.ThrowsExactly<ArgumentException>(() => cache.StoreMetadata("other", Envelope("root", 1)));
        cache.StoreMetadata("root", Envelope("root", 2));
        CollectionAssert.AreEqual(Envelope("root", 2), lease.Data["root"]);
    }

    [TestMethod]
    public void TargetCache_IsNotSecurityState()
    {
        var cache = new DurableTufCache(new Lease());
        cache.StoreTarget("catalog.json", [1, 2]);
        Assert.IsNull(cache.LoadTarget("catalog.json"));
    }

    private static byte[] Envelope(string type, int version) => Encoding.UTF8.GetBytes(
        $$"""{"signed":{"_type":"{{type}}","version":{{version}}},"signatures":[{}]}""");

    private sealed class Lease : ITrustMetadataLease
    {
        public Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);
        public bool FailWrites { get; set; }
        public byte[]? LoadMetadata(string role) => Data.GetValueOrDefault(role);
        public void StoreMetadata(string role, ReadOnlySpan<byte> data)
        {
            if (FailWrites) throw new IOException("Persistence failed.");
            Data[role] = data.ToArray();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task VerifyHeldAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

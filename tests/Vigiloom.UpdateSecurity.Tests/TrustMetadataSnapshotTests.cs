using System.Security.Cryptography;
using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class TrustMetadataSnapshotTests
{
    [TestMethod]
    public void Snapshot_RoundTripsWithoutExposingMutableState()
    {
        var root = Envelope("root", 1);
        var original = TrustMetadataSnapshot.Provision(root);
        root[0] = 0;
        var updated = original.WithMetadata("timestamp", Envelope("timestamp", 1));
        Assert.IsNull(original.Read("timestamp"));
        var (data, digest) = updated.Encode();
        var restored = TrustMetadataSnapshot.Decode(data, digest);
        CollectionAssert.AreEqual(Envelope("root", 1), restored.Read("root"));
        var returned = restored.Read("root")!;
        returned[0] = 0;
        CollectionAssert.AreEqual(Envelope("root", 1), restored.Read("root"));
    }

    [TestMethod]
    public void Snapshot_RejectsCorruptionAndIncompleteState()
    {
        var (data, digest) = TrustMetadataSnapshot.Provision(Envelope("root", 1)).Encode();
        data[0] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => TrustMetadataSnapshot.Decode(data, digest));
        var missingRoot = Encoding.UTF8.GetBytes("{}");
        Assert.ThrowsExactly<InvalidDataException>(() => TrustMetadataSnapshot.Decode(missingRoot, SHA256.HashData(missingRoot)));
    }

    [TestMethod]
    public void Snapshot_RejectsRollbackAndChangedContentAtSameVersion()
    {
        var snapshot = TrustMetadataSnapshot.Provision(Envelope("root", 2));
        Assert.ThrowsExactly<InvalidDataException>(() => snapshot.WithMetadata("root", Envelope("root", 1)));
        var changed = Encoding.UTF8.GetBytes("""{"signed":{"_type":"root","version":2,"other":1},"signatures":[{}]}""");
        Assert.ThrowsExactly<InvalidDataException>(() => snapshot.WithMetadata("root", changed));
        var next = snapshot.WithMetadata("root", Envelope("root", 3));
        CollectionAssert.AreEqual(Envelope("root", 3), next.Read("root"));
        CollectionAssert.AreEqual(Envelope("root", 2), snapshot.Read("root"));
    }

    private static byte[] Envelope(string type, int version) => Encoding.UTF8.GetBytes(
        $$"""{"signed":{"_type":"{{type}}","version":{{version}}},"signatures":[{}]}""");
}

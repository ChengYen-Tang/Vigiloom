using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class IntegrityChunkTests
{
    private static IntegrityChunk Chunk()
    {
        var items = new[] { new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["etc", "config"]), IntegrityItemKind.File, new string('a', 64), new string('b', 64), "uid:1000") };
        var measured = IntegrityDigest.MeasureChunk(items);
        return new(6, Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), 1, Guid.CreateVersion7(), 1,
            DateTimeOffset.UtcNow, IntegrityScanOutcome.Complete, false, null, 0, 1, items.Length, measured.Bytes,
            IntegrityDigest.Combine([measured.Digest]), items);
    }

    [TestMethod]
    public void Codec_RoundTripsBoundedScopeAndRejectsInvalidContent()
    {
        var chunk = Chunk();
        Assert.IsTrue(IntegrityChunkCodec.TryRead(IntegrityChunkCodec.Serialize(chunk), out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(chunk.ScopeId, parsed.ScopeId);
        Assert.AreEqual(chunk.Items[0], parsed.Items[0]);
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { ScopeRevision = 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { Items = [chunk.Items[0] with { Key = "../secret" }] }));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { Items = [chunk.Items[0] with { Key = "f/+%63onfig" }] }));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { Items = [chunk.Items[0] with { ContentDigest = "raw secret" }] }));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { Items = [chunk.Items[0] with { ContentDigest = null }] }));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { Outcome = IntegrityScanOutcome.PermissionDenied }));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityChunkCodec.Serialize(chunk with { Outcome = IntegrityScanOutcome.Partial }));
        Assert.IsTrue(IntegrityChunkCodec.TryRead(IntegrityChunkCodec.Serialize(chunk with { Outcome = IntegrityScanOutcome.Partial,
            CoverageGap = true, PartialReason = IntegrityPartialReason.AccessDenied }), out var partial));
        Assert.IsTrue(partial!.CoverageGap);
        Assert.AreEqual(IntegrityPartialReason.AccessDenied, partial.PartialReason);
    }

    [TestMethod]
    public void Keys_AreTypedCanonicalAndHandleRegistryDefaultValues()
    {
        var file = IntegrityKey.Create(IntegrityItemKind.File, ["dir", "file:name"]);
        Assert.IsTrue(IntegrityKey.IsValid(IntegrityItemKind.File, file));
        Assert.IsFalse(IntegrityKey.IsValid(IntegrityItemKind.RegistryValue, file));
        var defaultValue = IntegrityKey.Create(IntegrityItemKind.RegistryValue, ["Software", ""]);
        Assert.IsTrue(IntegrityKey.IsValid(IntegrityItemKind.RegistryValue, defaultValue));
        Assert.IsFalse(IntegrityKey.IsValid(IntegrityItemKind.File, "f/+../+secret"));
        Assert.IsFalse(IntegrityKey.IsValid(IntegrityItemKind.File, @"C:\secret"));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityKey.Create(IntegrityItemKind.File, ["\uD800"]));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityKey.Create(IntegrityItemKind.File, ["\uDC00"]));
        Assert.IsTrue(IntegrityKey.IsValid(IntegrityItemKind.File, IntegrityKey.Create(IntegrityItemKind.File, ["\U0001F600"])));
    }

    [TestMethod]
    public void Digest_ChangesWhenContentOrChunkOrderChanges()
    {
        var chunk = Chunk();
        var first = IntegrityDigest.MeasureChunk(chunk.Items);
        var second = IntegrityDigest.MeasureChunk([chunk.Items[0] with { Owner = "uid:1001" }]);
        CollectionAssert.AreNotEqual(first.Digest, second.Digest);
        CollectionAssert.AreNotEqual(IntegrityDigest.Combine([first.Digest, second.Digest]), IntegrityDigest.Combine([second.Digest, first.Digest]));
        Assert.ThrowsExactly<ArgumentException>(() => IntegrityDigest.Combine([new byte[31]]));
    }
}

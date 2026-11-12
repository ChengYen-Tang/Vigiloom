using System.Text;
using System.Security.Cryptography;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class InventoryChunkTests
{
    [TestMethod]
    public void Digest_Schema4InventoryEntry_MatchesPersistedWireRepresentation()
    {
        // This is the schema-4 representation already persisted in existing snapshots.
        const string persisted = "[{\"key\":\"package:amd64\",\"name\":\"package\",\"version\":\"1:2.0-1\",\"architecture\":\"amd64\",\"supplier\":\"maintainer\",\"numericId\":null,\"primaryGroupId\":null,\"members\":null,\"installedOn\":null,\"operatingSystem\":null,\"processArchitecture\":null,\"runtime\":null,\"availableProcessorCount\":null,\"operatingSystemFamily\":null,\"distributionId\":null,\"distributionVersion\":null,\"bootedAt\":null,\"agentVersion\":null,\"totalMemoryBytes\":null,\"resourceType\":null,\"parentKey\":null,\"address\":null,\"capacityBytes\":null,\"hardwareType\":null}]";
        var bytes = Encoding.UTF8.GetBytes(persisted);
        var actual = InventoryDigest.MeasureChunk([new("package:amd64", "package", "1:2.0-1", "amd64", "maintainer")]);

        Assert.AreEqual(bytes.Length, actual.Bytes);
        CollectionAssert.AreEqual(SHA256.HashData(bytes), actual.Digest);
    }

    [TestMethod]
    public void Codec_Schema4RejectsFieldsIntroducedForProcessSnapshots()
    {
        var chunk = Create();
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(
            chunk with { Items = [chunk.Items[0] with { CommandLine = "sensitive" }] }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(
            chunk with { Source = InventorySource.HostProcesses }));
    }

    [TestMethod]
    public void Codec_PartialReasonIsBoundedAndOnlyAllowedForNewPartialSnapshots()
    {
        var item = new InventoryEntry("linux:boot:42:7", "worker", NumericId: 42, ProcessStartedAt: DateTimeOffset.UtcNow);
        var chunk = Create() with { SchemaVersion = 5, Source = InventorySource.HostProcesses, Outcome = InventoryOutcome.Partial,
            Items = [item], PartialReason = InventoryPartialReason.AccessDenied | InventoryPartialReason.Truncated };
        Assert.IsTrue(InventoryChunkCodec.TryRead(InventoryChunkCodec.Serialize(chunk), out var parsed));
        Assert.AreEqual(chunk.PartialReason, parsed!.PartialReason);
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { PartialReason = (InventoryPartialReason)32 }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Outcome = InventoryOutcome.Complete }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(Create() with { Outcome = InventoryOutcome.Partial, PartialReason = InventoryPartialReason.Other }));
    }

    [TestMethod]
    public void Codec_ProcessCommandRequiresFixedMaskedArguments()
    {
        var process = new InventoryEntry("linux:boot:42:7", "worker", NumericId: 42, ProcessStartedAt: DateTimeOffset.UtcNow,
            ExecutablePath: "/usr/bin/worker", CommandLine: "/usr/bin/worker" + InventoryChunkCodec.MaskedArgumentsSuffix);
        var chunk = Create() with { SchemaVersion = InventoryChunkCodec.ProcessSchemaVersion, Source = InventorySource.HostProcesses, Items = [process] };
        Assert.IsTrue(InventoryChunkCodec.TryRead(InventoryChunkCodec.Serialize(chunk), out _));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with
        {
            Items = [process with { CommandLine = "/usr/bin/worker --password=secret" }]
        }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with
        {
            Items = [process with { ExecutablePath = null }]
        }));
    }

    [TestMethod]
    public void Codec_RoundTripsSourceAndExplicitEmptySnapshot()
    {
        var chunk = Create() with { TotalItems = 0, TotalBytes = 2, Items = [] };
        Assert.IsTrue(InventoryChunkCodec.TryRead(InventoryChunkCodec.Serialize(chunk), out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(InventoryOutcome.Complete, parsed.Outcome);
        Assert.AreEqual(InventorySource.LinuxDpkg, parsed.Source);
        Assert.IsEmpty(parsed.Items);
    }

    [TestMethod]
    public void Codec_RejectsMalformedAndConflictingChunkMetadata()
    {
        var chunk = Create();
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Generation = 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { ChunkIndex = 1 }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Digest = new byte[31] }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Outcome = InventoryOutcome.PermissionDenied }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { TotalItems = 2, Items = [chunk.Items[0], chunk.Items[0]] }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Items = [chunk.Items[0] with { Name = "bad\nname" }] }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Items = [chunk.Items[0] with { Architecture = null }] }));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk with { Source = InventorySource.LinuxAccounts }));
        var json = Encoding.UTF8.GetString(InventoryChunkCodec.Serialize(chunk));
        Assert.IsFalse(InventoryChunkCodec.TryRead(Encoding.UTF8.GetBytes(json.Insert(1, "\"unexpected\":true,")), out _));
        Assert.IsFalse(InventoryChunkCodec.TryRead(Encoding.UTF8.GetBytes(json.Insert(1, "\"schemaVersion\":4,")), out _));
        Assert.IsFalse(InventoryChunkCodec.TryRead("{\"schemaVersion\":4}"u8, out _));
    }

    [TestMethod]
    public void Digest_ProtectsChunkOrderAndContent()
    {
        var chunk = Create();
        var first = InventoryDigest.MeasureChunk(chunk.Items);
        var second = InventoryDigest.MeasureChunk([chunk.Items[0] with { Version = "2" }]);
        Assert.IsGreaterThan(0, first.Bytes);
        CollectionAssert.AreNotEqual(first.Digest, second.Digest);
        var forward = InventoryDigest.Combine([first.Digest, second.Digest]);
        var reverse = InventoryDigest.Combine([second.Digest, first.Digest]);
        CollectionAssert.AreNotEqual(forward, reverse);
        Assert.ThrowsExactly<ArgumentException>(() => InventoryDigest.Combine([new byte[31]]));
    }

    [TestMethod]
    public void Digest_RoundTripsMembersAndNullableFields()
    {
        var group = new InventoryEntry("group", "group", NumericId: 100, Members: ["alice", "bob"]);
        var chunk = Create() with { Source = InventorySource.LinuxGroups, Items = [group] };
        var before = InventoryDigest.MeasureChunk(chunk.Items);
        Assert.IsTrue(InventoryChunkCodec.TryRead(InventoryChunkCodec.Serialize(chunk), out var parsed));
        Assert.IsNotNull(parsed);
        var after = InventoryDigest.MeasureChunk(parsed.Items);
        Assert.AreEqual(before.Bytes, after.Bytes);
        CollectionAssert.AreEqual(before.Digest, after.Digest);
    }

    [TestMethod]
    public void Encoding_StopsWhenAValidGroupExceedsOneChunk()
    {
        var members = Enumerable.Repeat(new string('組', InventoryChunkCodec.MaximumMemberLength), InventoryChunkCodec.MaximumMembers).ToArray();
        var group = new InventoryEntry("group", "group", NumericId: 100, Members: members);
        var chunk = Create() with { Source = InventorySource.LinuxGroups, Items = [group] };
        Assert.ThrowsExactly<ArgumentException>(() => InventoryChunkCodec.Serialize(chunk));
        Assert.ThrowsExactly<ArgumentException>(() => InventoryDigest.MeasureChunk(chunk.Items));
    }

    private static InventoryChunk Create()
    {
        var now = DateTimeOffset.UtcNow;
        return new(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
            InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 1, 100, new byte[32],
            [new("package:amd64", "package", "1:2.0-1", "amd64", "maintainer")]);
    }
}

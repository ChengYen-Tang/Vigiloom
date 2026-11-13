using Vigiloom.Agent.Core.Integrity;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class IntegrityHashTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FileAsync_StopsAtObservedLengthAndDetectsEarlyEnd()
    {
        await using var input = new MemoryStream([1, 2, 3, 4]);
        var first = await IntegrityHash.FileAsync(input, 3, TestContext.CancellationToken);
        Assert.AreEqual(3L, first.BytesRead);
        CollectionAssert.AreEqual(System.Security.Cryptography.SHA256.HashData([1, 2, 3]), first.Digest);
        Assert.AreEqual(3L, input.Position);
        var incomplete = await IntegrityHash.FileAsync(input, 2, TestContext.CancellationToken);
        Assert.IsNull(incomplete.Digest);
        Assert.AreEqual(1L, incomplete.BytesRead);
    }

    [TestMethod]
    public void RegistryValue_IncludesTypeAndDistinguishesContent()
    {
        byte[] value = [1, 2, 3];
        var first = IntegrityHash.RegistryValue(1, value);
        Assert.AreEqual(first, IntegrityHash.RegistryValue(1, value));
        Assert.AreNotEqual(first, IntegrityHash.RegistryValue(2, value));
        Assert.AreNotEqual(first, IntegrityHash.RegistryValue(1, [1, 2]));
        Assert.AreEqual(64, first.Length);
    }
}

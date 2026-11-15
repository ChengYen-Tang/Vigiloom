using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class FindingIdentityTests
{
    [TestMethod]
    public void Hash_IsStableAndSeparatesFieldsAndCase()
    {
        var original = FindingIdentity.Hash("ab", "c");
        CollectionAssert.AreEqual(original, FindingIdentity.Hash("ab", "c"));
        Assert.IsFalse(original.AsSpan().SequenceEqual(FindingIdentity.Hash("a", "bc")));
        Assert.IsFalse(original.AsSpan().SequenceEqual(FindingIdentity.Hash("AB", "c")));
        Assert.Throws<ArgumentException>(() => FindingIdentity.Hash("", "c"));
    }
}

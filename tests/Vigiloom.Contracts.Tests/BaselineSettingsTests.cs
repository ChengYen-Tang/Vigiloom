using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class BaselineSettingsTests
{
    [TestMethod]
    public void Revision_TracksPolicyAndIsIndependentOfSelectionOrder()
    {
        var check = new BaselineCheck("shadow", "Shadow permissions", "linux", ["ubuntu:24.04"],
            "file-permissions-no-extended-acl", "/etc/shadow", "0640-or-stricter", "https://example.test/rule", "BSD-3-Clause");
        var first = new BaselineSelection(Guid.CreateVersion7(), Guid.CreateVersion7(), "AQID", check);
        var second = new BaselineSelection(Guid.CreateVersion7(), Guid.CreateVersion7(), "AQID", check with { Id = "other" });
        var original = BaselineSettingsSnapshot.Create(21600, [first, second]);
        var reordered = BaselineSettingsSnapshot.Create(21600, [second, first]);
        var updated = BaselineSettingsSnapshot.Create(21600, [first with { RevisionId = Guid.CreateVersion7() }, second]);

        Assert.IsTrue(original.IsValid());
        Assert.AreEqual(original.Revision, reordered.Revision);
        Assert.AreNotEqual(original.Revision, updated.Revision);
        Assert.IsFalse((original with { ScanIntervalSeconds = 1 }).IsValid());
    }
}

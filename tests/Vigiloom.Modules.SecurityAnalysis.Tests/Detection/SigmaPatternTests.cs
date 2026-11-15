using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaPatternTests
{
    [TestMethod]
    [DataRow("powershell.exe", null, "PowerShell.EXE", true)]
    [DataRow("cmd?.exe", null, "cmd1.exe", true)]
    [DataRow("cmd?.exe", null, "cmd12.exe", false)]
    [DataRow(@"C:\\Windows\\*.exe", null, @"c:\Windows\cmd.exe", true)]
    [DataRow(@"file\?.txt", null, "file?.txt", true)]
    [DataRow(@"file\?.txt", null, "file1.txt", false)]
    [DataRow("admin", "contains", "pre-ADMIN-post", true)]
    [DataRow("admin", "startswith", "prefix-admin", false)]
    [DataRow("admin", "endswith", "prefix-admin", true)]
    public void Matches_UsesSigmaWildcardAndModifierSemantics(string value, string? modifier, string input, bool expected) =>
        Assert.AreEqual(expected, new SigmaPattern(value, modifier).Matches(input));

    [TestMethod]
    public void Constructor_RejectsUnsupportedModifier() =>
        Assert.ThrowsExactly<ArgumentException>(() => new SigmaPattern("value", "unrecognized"));
}

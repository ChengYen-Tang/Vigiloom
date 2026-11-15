using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaConditionTests
{
    [TestMethod]
    public void Compile_UsesSigmaPrecedenceAndMissingFieldState()
    {
        var rule = SigmaCondition.Compile("selection and not filter", Selections());
        Assert.AreEqual(SigmaMatch.Yes, rule(Event(new() { ["EventID"] = ["4625"], ["Account"] = ["alice"] })));
        Assert.AreEqual(SigmaMatch.No, rule(Event(new() { ["EventID"] = ["4625"], ["Account"] = ["system"] })));
        Assert.AreEqual(SigmaMatch.Unknown, rule(Event(new() { ["EventID"] = ["4625"] })));
        Assert.AreEqual(SigmaMatch.No, rule(Event(new() { ["EventID"] = ["4624"] })));
    }

    [TestMethod]
    public void Compile_ThemExcludesPrivateSelectionsAndSupportsWildcardPatterns()
    {
        var selections = Selections();
        selections.Add("_internal", [[new SigmaFieldCondition("Unexpected", [new("value")])]]);
        var log = Event(new() { ["EventID"] = ["4625"], ["Account"] = ["alice"] });
        Assert.AreEqual(SigmaMatch.Yes, SigmaCondition.Compile("1 of them", selections)(log));
        Assert.AreEqual(SigmaMatch.Yes, SigmaCondition.Compile("1 of sel*ion", selections)(log));
        Assert.AreEqual(SigmaMatch.Unknown, SigmaCondition.Compile("all of them", selections)(Event(new() { ["EventID"] = ["4625"] })));
    }

    [TestMethod]
    public void Compile_RejectsUnknownSelectionsAndUnsupportedSyntax()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => SigmaCondition.Compile("selection and unknown", Selections()));
        Assert.ThrowsExactly<InvalidDataException>(() => SigmaCondition.Compile("2 of selection*", Selections()));
        Assert.ThrowsExactly<InvalidDataException>(() => SigmaCondition.Compile("(selection", Selections()));
        Assert.ThrowsExactly<InvalidDataException>(() => SigmaCondition.Compile(string.Concat(Enumerable.Repeat("not ", 256)) + "selection", Selections()));
    }

    private static Dictionary<string, SigmaFieldCondition[][]> Selections() => new(StringComparer.Ordinal)
    {
        ["selection"] = [[new SigmaFieldCondition("EventID", [new("4625")])]],
        ["filter"] = [[new SigmaFieldCondition("Account", [new("system")])]]
    };

    private static HostEvent Event(Dictionary<string, List<string>> fields) => new(HostEventStatus.Ready, AgentLogSourceKind.WindowsEvent, fields);
}

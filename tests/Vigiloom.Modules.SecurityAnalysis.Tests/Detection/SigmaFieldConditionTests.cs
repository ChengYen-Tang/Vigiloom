using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaFieldConditionTests
{
    [TestMethod]
    public void Evaluate_ValuesRespectOrAndAll()
    {
        var log = Event(new() { ["CommandLine"] = ["cmd.exe /c whoami"] });
        SigmaPattern[] alternatives = [new("powershell", "contains"), new("WHOAMI", "contains")];
        Assert.AreEqual(SigmaMatch.Yes, new SigmaFieldCondition("CommandLine", alternatives).Evaluate(log));
        Assert.AreEqual(SigmaMatch.No, new SigmaFieldCondition("CommandLine", alternatives, all: true).Evaluate(log));
    }

    [TestMethod]
    public void Evaluate_MissingAndOpaqueFieldsDoNotBecomeFalseEvidence()
    {
        var noFields = Event([]);
        var equals = new SigmaFieldCondition("TargetUserName", [new("alice")]);
        Assert.AreEqual(SigmaMatch.Unknown, equals.Evaluate(noFields));
        var absent = new SigmaFieldCondition("TargetUserName", [], exists: false);
        Assert.AreEqual(SigmaMatch.Yes, absent.Evaluate(noFields));
        Assert.AreEqual(SigmaMatch.Unknown, absent.Evaluate(noFields with { HasOpaqueFields = true }));
        Assert.AreEqual(SigmaMatch.Unknown, absent.Evaluate(noFields with { Status = HostEventStatus.Gap }));
    }

    private static HostEvent Event(Dictionary<string, List<string>> fields) => new(HostEventStatus.Ready, AgentLogSourceKind.TextFile, fields);
}

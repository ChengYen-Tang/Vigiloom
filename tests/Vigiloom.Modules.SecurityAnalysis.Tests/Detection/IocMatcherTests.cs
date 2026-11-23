using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class IocMatcherTests
{
    [TestMethod]
    public void Match_UsesStructuredFieldsAndKeepsSourceLines()
    {
        var matcher = new IocMatcher(Encoding.UTF8.GetBytes("# hash\nD41D8CD98F00B204E9800998ECF8427E;test\n"),
            Encoding.UTF8.GetBytes("# address\n192.0.2.1\nExample.org\n"));
        var hostEvent = new HostEvent(HostEventStatus.Ready, AgentLogSourceKind.WindowsEvent,
            new Dictionary<string, List<string>>
            {
                ["DestinationIp"] = ["192.0.2.1"],
                ["QueryName"] = ["EXAMPLE.ORG", "sub.example.org"],
                ["Hashes"] = ["SHA256=invalid,MD5=D41D8CD98F00B204E9800998ECF8427E"],
                ["CommandLine"] = ["example.org"]
            });

        var matches = matcher.Match(hostEvent).ToArray();
        CollectionAssert.AreEquivalent(new[] { "ip:2", "domain:3", "md5:2" },
            matches.Select(x => $"{x.Kind}:{x.SourceLine}").ToArray());
    }

    [TestMethod]
    public void Match_DoesNotTreatMissingOrInvalidEventsAsCleanMatches()
    {
        var matcher = new IocMatcher([], "192.0.2.1"u8.ToArray());
        var fields = new Dictionary<string, List<string>> { ["DestinationIp"] = ["192.0.2.1"] };
        Assert.IsEmpty(matcher.Match(new(HostEventStatus.Gap, AgentLogSourceKind.WindowsEvent, fields)));
        Assert.IsEmpty(matcher.Match(new(HostEventStatus.Invalid, AgentLogSourceKind.WindowsEvent, fields)));
    }
}

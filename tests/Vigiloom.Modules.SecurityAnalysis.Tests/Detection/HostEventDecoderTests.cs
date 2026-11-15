using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class HostEventDecoderTests
{
    [TestMethod]
    public void Decode_WindowsXml_PreservesEventFieldsWithoutInventingNames()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="Microsoft-Windows-Security-Auditing"/><EventID>4625</EventID><Channel>Security</Channel><Execution ProcessID="10"/><Security UserID="S-1-5-18"/></System>
              <EventData><Data Name="TargetUserName">alice</Data><Data Name="IpAddress">192.0.2.1</Data><Data Name="IpAddress">192.0.2.2</Data></EventData>
            </Event>
            """;
        var result = HostEventDecoder.Decode(Log(AgentLogSourceKind.WindowsEvent, xml));
        Assert.AreEqual(HostEventStatus.Ready, result.Status);
        Assert.AreEqual("4625", result.Fields["EventID"].Single());
        Assert.AreEqual("Security", result.Fields["Channel"].Single());
        Assert.AreEqual("Security", result.Fields["EventLog"].Single());
        Assert.AreEqual("alice", result.Fields["TargetUserName"].Single());
        CollectionAssert.AreEqual(new[] { "192.0.2.1", "192.0.2.2" }, result.Fields["IpAddress"]);
        Assert.IsFalse(result.Fields.ContainsKey("Image"));
        Assert.IsFalse(result.Fields.ContainsKey("User"));
    }

    [TestMethod]
    public void Decode_Journal_PreservesTextAndMarksBinaryFields()
    {
        const string json = """[{"Encoding":"utf8","Value":"MESSAGE=authentication failed"},{"Encoding":"utf8","Value":"_PID=42"},{"Encoding":"base64","Value":"AP8="}]""";
        var result = HostEventDecoder.Decode(Log(AgentLogSourceKind.Journal, json));
        Assert.AreEqual(HostEventStatus.Ready, result.Status);
        Assert.AreEqual("authentication failed", result.Fields["MESSAGE"].Single());
        Assert.AreEqual("42", result.Fields["_PID"].Single());
        Assert.IsTrue(result.HasOpaqueFields);
    }

    [TestMethod]
    public void Decode_GapAndMalformedPayload_AreNotNormalEvents()
    {
        var gap = Log(AgentLogSourceKind.TextFile, "") with { Gap = new(AgentLogGapReason.CursorLost, null) };
        Assert.AreEqual(HostEventStatus.Gap, HostEventDecoder.Decode(gap).Status);
        Assert.AreEqual(HostEventStatus.Invalid, HostEventDecoder.Decode(Log(AgentLogSourceKind.WindowsEvent, "<broken>")).Status);
        Assert.AreEqual(HostEventStatus.Invalid, HostEventDecoder.Decode(Log(AgentLogSourceKind.Journal, "{}" )).Status);
    }

    [TestMethod]
    public void Decode_WindowsPayloadCannotOverrideSystemEventId()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventID>4625</EventID></System>
            <EventData><Data Name="EventID">4688</Data></EventData></Event>
            """;
        Assert.AreEqual(HostEventStatus.Invalid, HostEventDecoder.Decode(Log(AgentLogSourceKind.WindowsEvent, xml)).Status);
    }

    private static HostLogEntry Log(AgentLogSourceKind kind, string content) => new(Guid.CreateVersion7(), Guid.CreateVersion7(),
        Guid.CreateVersion7(), kind, "test", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, content, null, null, null, null, null, null);
}

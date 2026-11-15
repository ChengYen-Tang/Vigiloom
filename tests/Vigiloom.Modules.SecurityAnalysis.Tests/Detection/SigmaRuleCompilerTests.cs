using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class SigmaRuleCompilerTests
{
    private const string Rule = """
        title: Suspicious authentication failure
        id: 83888275-dad0-4f55-9d92-a8c24ef76856
        logsource:
          product: windows
          service: security
        detection:
          selection_event:
            EventID: [4625, 4776]
          selection_status:
            - Status: ['0xC0000072', '0xC000006F']
            - SubStatus: ['0xC0000072', '0xC000006F']
          filter:
            SubjectUserSid: 'S-1-0-0'
          condition: all of selection_* and not filter
        """;

    [TestMethod]
    public void Compile_MatchesStandardRuleWithAlternativeMappings()
    {
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(Rule));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        Assert.AreEqual("windows", compiled.Rule.Metadata.Product);
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["EventID"] = ["4625"], ["Channel"] = ["Security"], ["SubStatus"] = ["0xc0000072"], ["SubjectUserSid"] = ["S-1-5-18"]
        };
        var log = new HostEvent(HostEventStatus.Ready, AgentLogSourceKind.WindowsEvent, fields);
        Assert.AreEqual(SigmaMatch.Yes, compiled.Rule.Match(log));
        fields["SubjectUserSid"] = ["S-1-0-0"];
        Assert.AreEqual(SigmaMatch.No, compiled.Rule.Match(log));
        fields.Remove("SubjectUserSid");
        Assert.AreEqual(SigmaMatch.Unknown, compiled.Rule.Match(log));
        fields["Channel"] = ["System"];
        Assert.AreEqual(SigmaMatch.No, compiled.Rule.Match(log));
    }

    [TestMethod]
    public void Compile_RejectsUnsupportedModifierInsteadOfIgnoringIt()
    {
        var result = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(Rule.Replace("EventID:", "EventID|re:", StringComparison.Ordinal)));
        Assert.IsNull(result.Rule);
        Assert.IsNotNull(result.Error);
        Assert.Contains("Unsupported Sigma modifier", result.Error);
    }

    [TestMethod]
    public void Compile_RejectsUnsupportedLogSource()
    {
        var unsupported = Rule.Replace("service: security", "category: image_load", StringComparison.Ordinal);
        var result = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(unsupported));
        Assert.IsNull(result.Rule);
        Assert.AreEqual("Unsupported Sigma log source.", result.Error);
    }

    [TestMethod]
    public void Compile_ProcessCreation_UsesWindowsSecurityFields()
    {
        const string yaml = """
            title: Suspicious process
            logsource:
              product: windows
              category: process_creation
            detection:
              selection:
                Image|endswith: '\powershell.exe'
              condition: selection
            """;
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Security-Auditing"/><EventID>4688</EventID><Channel>Security</Channel></System>
            <EventData><Data Name="NewProcessName">C:\Windows\System32\powershell.exe</Data></EventData></Event>
            """;
        var entry = new HostLogEntry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
            "test", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, xml, null, null, null, null, null, null);
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        Assert.AreEqual(SigmaMatch.Yes, compiled.Rule.Match(HostEventDecoder.Decode(entry)));
        var wrongProvider = entry with { Content = xml.Replace("Microsoft-Windows-Security-Auditing", "Other-Provider", StringComparison.Ordinal) };
        Assert.AreEqual(SigmaMatch.No, compiled.Rule.Match(HostEventDecoder.Decode(wrongProvider)));
    }

    [TestMethod]
    public void Compile_KeywordRule_MatchesJournalMessage()
    {
        const string yaml = """
            title: Suspicious ssh log
            logsource:
              product: linux
              service: sshd
            detection:
              keywords:
                - 'failed password'
                - 'invalid user'
              condition: keywords
            """;
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["SYSLOG_IDENTIFIER"] = ["sshd"], ["MESSAGE"] = ["Failed password for user"]
        };
        var log = new HostEvent(HostEventStatus.Ready, AgentLogSourceKind.Journal, fields);
        Assert.AreEqual(SigmaMatch.Yes, compiled.Rule.Match(log));
        fields["MESSAGE"] = ["Accepted password"];
        Assert.AreEqual(SigmaMatch.No, compiled.Rule.Match(log));
        fields.Remove("MESSAGE");
        Assert.AreEqual(SigmaMatch.Unknown, compiled.Rule.Match(log));
    }

    [TestMethod]
    public void Compile_KeywordRule_SearchesDecodedWindowsUserData()
    {
        const string yaml = """
            title: Log cleared
            logsource:
              product: windows
              service: security
            detection:
              keywords:
                - 'alice & bob'
              condition: keywords
            """;
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventID>1102</EventID><Channel>Security</Channel></System>
            <UserData><LogFileCleared xmlns="http://manifests.microsoft.com/win/2004/08/windows/eventlog"><SubjectUserName>alice &amp; bob</SubjectUserName></LogFileCleared></UserData></Event>
            """;
        var entry = new HostLogEntry(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
            "test", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, xml, null, null, null, null, null, null);
        var compiled = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml));
        Assert.IsNotNull(compiled.Rule, compiled.Error);
        Assert.AreEqual(SigmaMatch.Yes, compiled.Rule.Match(HostEventDecoder.Decode(entry)));
        var negated = SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(yaml.Replace("condition: keywords", "condition: not keywords", StringComparison.Ordinal)));
        Assert.IsNotNull(negated.Rule, negated.Error);
        Assert.AreEqual(SigmaMatch.No, negated.Rule.Match(HostEventDecoder.Decode(entry)));
    }

    [TestMethod]
    public void Compile_DistinguishesNullFromQuotedEmptyString()
    {
        var nullRule = Rule.Replace("EventID: [4625, 4776]", "EventID:", StringComparison.Ordinal);
        Assert.IsNull(SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(nullRule)).Rule);
        var emptyRule = Rule.Replace("EventID: [4625, 4776]", "EventID: ''", StringComparison.Ordinal);
        Assert.IsNotNull(SigmaRuleCompiler.Compile(Encoding.UTF8.GetBytes(emptyRule)).Rule);
    }
}

using System.Text.Json.Nodes;
using System.ComponentModel;
using Vigiloom.Agent.Platforms.Windows.Logs;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class EventLogSourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Integration")]
    public async Task SecurityChannel_ReportsActualReadPermissionWithoutElevation()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        try
        {
            var result = EventLogSource.ReadNext("Security", null, TestContext.CancellationToken);
            Assert.IsTrue(result.Status is EventLogReadStatus.Record or EventLogReadStatus.End or EventLogReadStatus.Oversized);
            TestContext.WriteLine("Security channel is readable with the current process token.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            TestContext.WriteLine("Security channel access denied by the current process token.");
            Assert.AreEqual(5, exception.NativeErrorCode);
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
            {
                if (OperatingSystem.IsWindows()) await new WindowsEventLogReader("Security").ReadAsync(null, TestContext.CancellationToken);
            });
        }
    }

    [TestMethod]
    public void Parse_LeavesUnavailableMetadataUnknownWhileKeepingRawXml()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var provider = new string('x', 257);
        var xml = $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><Provider Name=\"{provider}\"/><EventRecordID>12</EventRecordID><EventID>invalid</EventID><Level></Level><Security UserID=\"bad&#x7F;value\"/></System></Event>";
        var record = EventLogSource.Parse(12, xml);
        Assert.AreEqual(xml, record.Xml);
        Assert.IsNull(record.EventId);
        Assert.IsNull(record.Provider);
        Assert.IsNull(record.Level);
        Assert.IsNull(record.User);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ReadNext_OversizedEventHasResumableBoundedIdentity()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        var first = EventLogSource.ReadNext("Application", null, 1, TestContext.CancellationToken);
        if (first.Status == EventLogReadStatus.End) { Assert.Inconclusive("No readable Application event."); return; }
        Assert.AreEqual(EventLogReadStatus.Oversized, first.Status);
        Assert.IsNotNull(first.NextCursor);
        var next = EventLogSource.ReadNext("Application", first.NextCursor, 1, TestContext.CancellationToken);
        Assert.AreNotEqual(EventLogReadStatus.CursorMissing, next.Status);
        var altered = JsonNode.Parse(first.NextCursor)!.AsObject();
        altered["Digest"] = new string('0', 64);
        Assert.AreEqual(EventLogReadStatus.CursorMissing, EventLogSource.ReadNext("Application", altered.ToJsonString(), 1, TestContext.CancellationToken).Status);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void NativeRender_ChecksRequiredSizeBeforeAllocatingXml()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        using var query = new NativeEventQuery("Application");
        using var record = query.Next();
        if (record is null) { Assert.Inconclusive("No readable Application event."); return; }
        var small = NativeEventQuery.RenderXml(record, 1);
        Assert.IsTrue(small.Oversized);
        Assert.IsNull(small.Text);
        Assert.IsGreaterThan(4U, small.RequiredBytes);
        var full = NativeEventQuery.RenderXml(record, 65_536);
        if (full.Oversized) { Assert.Inconclusive("First event exceeds the supported XML size."); return; }
        Assert.IsNotNull(full.Text);
        Assert.StartsWith("<Event", full.Text);
        Assert.AreEqual((uint)((full.Text.Length + 1) * 2), full.RequiredBytes);
        var identity = NativeEventQuery.SystemIdentity(record);
        Assert.IsGreaterThan(0UL, identity.RecordId);
        Assert.IsGreaterThan(0UL, identity.CreatedFileTime);
        var bookmark = NativeEventQuery.Bookmark(record);
        using var reopened = new NativeEventQuery("Application");
        reopened.Seek(bookmark);
        using var selected = reopened.Next();
        Assert.IsNotNull(selected);
        Assert.AreEqual(bookmark, NativeEventQuery.Bookmark(selected));
        Assert.AreEqual(full.Text, NativeEventQuery.RenderXml(selected, 65_536).Text);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Reader_MapsMissingChannelToSourceUnavailable()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        var reader = new WindowsEventLogReader($"Vigiloom-absent-{Guid.CreateVersion7():N}");
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            if (OperatingSystem.IsWindows()) await reader.ReadAsync(null, TestContext.CancellationToken);
        });
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Reader_ReportsMissingCursorBeforeReplayingFirstAvailableEvent()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        var reader = new WindowsEventLogReader("Application");
        var first = await reader.ReadAsync(null, TestContext.CancellationToken);
        if (first.Status != LogReadStatus.Record) { Assert.Inconclusive("No readable first event."); return; }
        Assert.IsNotNull(first.NextCursor);
        var bookmark = JsonNode.Parse(NativeLogCursor.Decode(first.NextCursor)!)!.AsObject();
        bookmark["Digest"] = new string('0', 64);
        var gap = await reader.ReadAsync(NativeLogCursor.Encode(bookmark.ToJsonString()), TestContext.CancellationToken);
        Assert.AreEqual(LogReadStatus.Gap, gap.Status);
        Assert.AreEqual(AgentLogGapReason.CursorLost, gap.Gap!.Reason);
        Assert.IsNull(gap.Gap.AffectedCount);
        Assert.AreEqual(NativeLogCursor.Restart, gap.NextCursor);
        var restarted = await reader.ReadAsync(gap.NextCursor, TestContext.CancellationToken);
        Assert.AreEqual(LogReadStatus.Record, restarted.Status);
        Assert.AreEqual(first.Record!.Content, restarted.Record!.Content);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public void ReadNext_ResumesAfterRealBookmarkAndDetectsChangedRecord()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        var token = TestContext.CancellationToken;
        var first = EventLogSource.ReadNext("Application", null, token);
        if (first.Status is EventLogReadStatus.End or EventLogReadStatus.Oversized)
        {
            Assert.Inconclusive("The Application log has no readable first record within the supported size.");
            return;
        }
        Assert.AreEqual(EventLogReadStatus.Record, first.Status);
        Assert.IsNotNull(first.Observation);
        Assert.IsNotNull(first.NextCursor);
        var resumed = EventLogSource.ReadNext("Application", first.NextCursor, token);
        if (resumed.Status == EventLogReadStatus.Record)
        {
            Assert.IsNotNull(resumed.Observation);
            Assert.IsGreaterThan(first.Observation.RecordId, resumed.Observation.RecordId);
        }
        else Assert.AreEqual(EventLogReadStatus.End, resumed.Status);

        var changed = JsonNode.Parse(first.NextCursor)!.AsObject();
        changed["Digest"] = new string('0', 64);
        Assert.AreEqual(EventLogReadStatus.CursorMissing, EventLogSource.ReadNext("Application", changed.ToJsonString(), token).Status);
    }

    [TestMethod]
    public void ReadNext_RejectsInvalidCursorAndCancellation()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows Event Log."); return; }
        Assert.ThrowsExactly<InvalidDataException>(() => { if (OperatingSystem.IsWindows()) EventLogSource.ReadNext("Application", "{}", CancellationToken.None); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => { if (OperatingSystem.IsWindows()) EventLogSource.ReadNext("Application", null, cancellation.Token); });
    }
}

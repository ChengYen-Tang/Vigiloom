using System.Text.Json;
using System.Text;
using Vigiloom.Agent.Platforms.Linux.Logs;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class JournalSourceTests
{
    [TestMethod]
    public void Metadata_ExtractsBoundedFieldsWithoutInventingOrTrustingBinaryValues()
    {
        var fields = new JournalField[]
        {
            new("utf8", "SYSLOG_IDENTIFIER=nginx"), new("utf8", "PRIORITY=3"),
            new("utf8", "_UID=33"), new("utf8", "_PID=120"), new("utf8", "MESSAGE=error\nsecond"),
            new("utf8", "SYSLOG_IDENTIFIER=another"), new("base64", "_UID=hidden")
        };
        var metadata = JournalMetadata.FromFields(fields);
        Assert.AreEqual("nginx", metadata.Provider);
        Assert.AreEqual("3", metadata.Level);
        Assert.AreEqual("33", metadata.User);
        Assert.AreEqual("120", metadata.Process);
        var invalid = JournalMetadata.FromFields([new("utf8", "SYSLOG_IDENTIFIER=bad\u007fvalue"),
            new("utf8", "PRIORITY=8"), new("utf8", "_UID=-1"), new("utf8", "_PID=not-a-number")]);
        Assert.IsNull(invalid.Provider); Assert.IsNull(invalid.Level); Assert.IsNull(invalid.User); Assert.IsNull(invalid.Process);
    }

    [TestMethod]
    public void EncodeField_RetainsMultilineUnicodeNulAndInvalidBytes()
    {
        byte[][] samples = [Encoding.UTF8.GetBytes("MESSAGE=測試\nsecond\0line"), [0x4d, 0x3d, 0xff, 0, 0xfe]];
        foreach (var original in samples)
        {
            var field = JournalFieldEncoding.Encode(original);
            var roundTrip = field.Encoding == "utf8" ? Encoding.UTF8.GetBytes(field.Value) : Convert.FromBase64String(field.Value);
            CollectionAssert.AreEqual(original, roundTrip);
        }
    }

    [TestMethod]
    public void ReadNext_RejectsInvalidCursorAndHonorsCancellationBeforeOpeningJournal()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        Assert.ThrowsExactly<ArgumentException>(() => { if (OperatingSystem.IsLinux()) JournalSource.ReadNext("\0", CancellationToken.None); });
        Assert.ThrowsExactly<ArgumentException>(() => { if (OperatingSystem.IsLinux()) JournalSource.ReadNext(new string('a', 4097), CancellationToken.None); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => { if (OperatingSystem.IsLinux()) JournalSource.ReadNext(null, cancellation.Token); });
    }

    [TestMethod]
    public void ReadNext_ResumesAfterARealAvailableEntry()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        JournalReadResult first;
        try { first = JournalSource.ReadNext(null, CancellationToken.None); }
        catch (DllNotFoundException) { Assert.Inconclusive("libsystemd is not installed."); return; }
        if (first.Status != JournalReadStatus.Record) { Assert.Inconclusive("No readable first journal entry."); return; }
        Assert.IsNotNull(first.Observation);
        Assert.IsNotNull(first.NextCursor);
        var fields = JsonSerializer.Deserialize<JournalField[]>(first.Observation.Content);
        Assert.IsNotNull(fields);
        Assert.IsNotEmpty(fields);
        Assert.IsTrue(fields.All(field => field.Encoding is "utf8" or "base64"));
        var next = JournalSource.ReadNext(first.NextCursor, CancellationToken.None);
        Assert.AreNotEqual(JournalReadStatus.CursorMissing, next.Status);
        if (next.NextCursor is not null) Assert.AreNotEqual(first.NextCursor, next.NextCursor);
    }
}

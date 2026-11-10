using System.Text;
using Vigiloom.Agent.Core.Logs;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class TextRecordParserTests
{
    [TestMethod]
    public void IndentedRecords_PreserveMultilineAndLeaveNextRecordUnconsumed()
    {
        var input = Encoding.UTF8.GetBytes("錯誤\r\n  詳情\n\t堆疊\n下一筆\n");
        var result = TextRecordParser.Parse(input, TextRecordMode.IndentedContinuation, flush: false);
        Assert.AreEqual(TextRecordStatus.Record, result.Status);
        Assert.AreEqual("錯誤\r\n  詳情\n\t堆疊\n", result.Content);
        Assert.AreEqual("下一筆\n", Encoding.UTF8.GetString(input.AsSpan(result.ConsumedBytes)));
    }

    [TestMethod]
    public void PartialCharacterAndUnterminatedLines_WaitUnlessExplicitlyFlushed()
    {
        var input = Encoding.UTF8.GetBytes("訊息\n 後續");
        var prefix = input.AsSpan(0, 1);
        Assert.AreEqual(TextRecordStatus.NeedMore, TextRecordParser.Parse(prefix, TextRecordMode.SingleLine, false).Status);
        Assert.AreEqual(TextRecordStatus.NeedMore, TextRecordParser.Parse(input, TextRecordMode.IndentedContinuation, false).Status);
        var flushed = TextRecordParser.Parse(input, TextRecordMode.IndentedContinuation, true);
        Assert.AreEqual(TextRecordStatus.Record, flushed.Status);
        Assert.AreEqual("訊息\n 後續", flushed.Content);
        Assert.AreEqual(input.Length, flushed.ConsumedBytes);
        var firstLine = TextRecordParser.Parse(input, TextRecordMode.SingleLine, false);
        Assert.AreEqual("訊息\n", firstLine.Content);
    }

    [TestMethod]
    public void InvalidOrOversizedRecord_DoesNotAdvancePosition()
    {
        byte[] bad = [0x66, 0xff, 0x0a, 0x6f, 0x6b, 0x0a];
        var invalid = TextRecordParser.Parse(bad, TextRecordMode.SingleLine, false);
        Assert.AreEqual(TextRecordStatus.InvalidEncoding, invalid.Status);
        Assert.AreEqual(0, invalid.ConsumedBytes);
        Assert.AreEqual(3, invalid.GapCandidateBytes);
        Assert.AreEqual("ok\n", TextRecordParser.Parse(bad.AsSpan(invalid.GapCandidateBytes!.Value), TextRecordMode.SingleLine, false).Content);
        var oversized = TextRecordParser.Parse(new byte[TextRecordParser.MaximumRecordBytes + 1], TextRecordMode.SingleLine, false);
        Assert.AreEqual(TextRecordStatus.Oversized, oversized.Status);
        Assert.AreEqual(0, oversized.ConsumedBytes);
        Assert.IsNull(oversized.GapCandidateBytes);
        var oversizedLine = new byte[TextRecordParser.MaximumRecordBytes + 4];
        oversizedLine[TextRecordParser.MaximumRecordBytes] = (byte)'\n';
        oversizedLine[^3] = (byte)'o'; oversizedLine[^2] = (byte)'k'; oversizedLine[^1] = (byte)'\n';
        var knownBoundary = TextRecordParser.Parse(oversizedLine, TextRecordMode.SingleLine, false);
        Assert.AreEqual(TextRecordParser.MaximumRecordBytes + 1, knownBoundary.GapCandidateBytes);
        Assert.AreEqual("ok\n", TextRecordParser.Parse(oversizedLine.AsSpan(knownBoundary.GapCandidateBytes!.Value), TextRecordMode.SingleLine, false).Content);
        var exact = Encoding.UTF8.GetBytes(new string('x', TextRecordParser.MaximumRecordBytes - 1) + "\n");
        Assert.AreEqual(TextRecordStatus.Record, TextRecordParser.Parse(exact, TextRecordMode.SingleLine, false).Status);
    }
}

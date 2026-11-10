using System.Text;
using Vigiloom.Agent.Core.Logs;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class TextDiscardScannerTests
{
    [TestMethod]
    public void OversizedSingleLine_StopsBeforeFollowingRecordAcrossChunks()
    {
        var first = TextDiscardScanner.Scan("partial"u8, TextRecordMode.SingleLine, false);
        Assert.AreEqual(7, first.ConsumedBytes);
        Assert.IsFalse(first.Complete);
        var second = TextDiscardScanner.Scan("end\nnext\n"u8, TextRecordMode.SingleLine, first.AtLineStart);
        Assert.AreEqual(4, second.ConsumedBytes);
        Assert.IsTrue(second.Complete);
    }

    [TestMethod]
    public void OversizedMultiline_PreservesBoundaryWhenSplitAtNewline()
    {
        var first = TextDiscardScanner.Scan("long\n continuation\n"u8, TextRecordMode.IndentedContinuation, false);
        Assert.IsFalse(first.Complete);
        Assert.IsTrue(first.AtLineStart);
        var bytes = Encoding.UTF8.GetBytes("\tmore\n下一筆\n");
        var second = TextDiscardScanner.Scan(bytes, TextRecordMode.IndentedContinuation, first.AtLineStart);
        Assert.IsTrue(second.Complete);
        Assert.AreEqual("\tmore\n", Encoding.UTF8.GetString(bytes.AsSpan(0, second.ConsumedBytes)));
        Assert.AreEqual("下一筆\n", Encoding.UTF8.GetString(bytes.AsSpan(second.ConsumedBytes)));
        var emptyContinuation = TextDiscardScanner.Scan("\nnext"u8, TextRecordMode.IndentedContinuation, true);
        Assert.AreEqual(0, emptyContinuation.ConsumedBytes);
        Assert.IsTrue(emptyContinuation.Complete);
    }
}

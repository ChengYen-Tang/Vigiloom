using System.Text;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class TextFileLogReaderTests(TestContext context)
{
    [TestMethod]
    public async Task Reader_StopsAtEachRecordAndReportsRotationBeforeRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-text-reader-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "active.log");
        var old = Path.Combine(directory, "old.log");
        try
        {
            await File.WriteAllTextAsync(path, "first\nsecond\n", context.CancellationToken);
            var reader = new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System);
            var first = await reader.ReadAsync(null, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Record, first.Status);
            Assert.AreEqual("first\n", first.Record!.Content);
            var second = await reader.ReadAsync(first.NextCursor, context.CancellationToken);
            Assert.AreEqual("second\n", second.Record!.Content);
            Assert.AreEqual(LogReadStatus.End, (await reader.ReadAsync(second.NextCursor, context.CancellationToken)).Status);
            File.Move(path, old);
            await File.WriteAllTextAsync(path, "new\n", context.CancellationToken);
            var gap = await reader.ReadAsync(second.NextCursor, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Gap, gap.Status);
            Assert.AreEqual(AgentLogGapReason.CursorLost, gap.Gap!.Reason);
            Assert.IsNull(gap.Gap.AffectedCount);
            var resumed = await reader.ReadAsync(gap.NextCursor, context.CancellationToken);
            Assert.AreEqual("new\n", resumed.Record!.Content);
        }
        finally { File.Delete(path); File.Delete(old); Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task Reader_DiscardsOversizedMultilineInBoundedChunksWithoutSkippingNextRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-text-reader-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "active.log");
        try
        {
            var oversized = new string('a', 140_000) + "\n continuation\n正常\n";
            await File.WriteAllTextAsync(path, oversized, context.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
            var reader = new TextFileLogReader(path, AgentTextLogMode.IndentedContinuation, TimeProvider.System);
            string? cursor = null;
            var gaps = 0;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var result = await reader.ReadAsync(cursor, context.CancellationToken);
                if (result.Status == LogReadStatus.Record)
                {
                    Assert.AreEqual("正常\n", result.Record!.Content);
                    Assert.IsGreaterThan(1, gaps);
                    return;
                }
                Assert.AreEqual(LogReadStatus.Gap, result.Status);
                Assert.AreEqual(AgentLogGapReason.Oversized, result.Gap!.Reason);
                Assert.IsNull(result.Gap.AffectedCount);
                Assert.IsNotNull(result.NextCursor);
                gaps++;
                cursor = result.NextCursor;
            }
            Assert.Fail("Reader did not progress to the next record.");
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task Reader_ClosesIdleOversizedUnterminatedRecordAndReadsLaterAppend()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-text-reader-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "active.log");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 70_000), context.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
            var reader = new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System);
            var first = await reader.ReadAsync(null, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Gap, first.Status);
            var second = await reader.ReadAsync(first.NextCursor, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Gap, second.Status);
            Assert.AreEqual(LogReadStatus.End, (await reader.ReadAsync(second.NextCursor, context.CancellationToken)).Status);
            await File.AppendAllTextAsync(path, "next\n", context.CancellationToken);
            var next = await reader.ReadAsync(second.NextCursor, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Record, next.Status);
            Assert.AreEqual("next\n", next.Record!.Content);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task Reader_ReportsInvalidUtf8WithoutConsumingTheFollowingLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-text-reader-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "active.log");
        try
        {
            await File.WriteAllBytesAsync(path, [0xff, (byte)'\n', .. Encoding.UTF8.GetBytes("valid\n")], context.CancellationToken);
            var reader = new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System);
            var gap = await reader.ReadAsync(null, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Gap, gap.Status);
            Assert.AreEqual(AgentLogGapReason.InvalidEncoding, gap.Gap!.Reason);
            Assert.AreEqual(1L, gap.Gap.AffectedCount);
            var next = await reader.ReadAsync(gap.NextCursor, context.CancellationToken);
            Assert.AreEqual("valid\n", next.Record!.Content);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}

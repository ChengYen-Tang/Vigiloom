using System.Text;
using System.Diagnostics;
using Vigiloom.Agent.Core.Logs;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class TextFileSourceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LinuxFifo_IsRejectedWithoutWaitingForAWriter()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-fifo-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pipe");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path }, UseShellExecute = false })!;
            await process.WaitForExitAsync(TestContext.CancellationToken);
            Assert.AreEqual(0, process.ExitCode);
            var read = Task.Run(() => TextFileSource.ReadAsync(path, null, TestContext.CancellationToken), TestContext.CancellationToken);
            await Assert.ThrowsExactlyAsync<IOException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [TestMethod]
    public async Task ReadsBoundedBytesAndDetectsRewriteTruncationAndReplacement()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Windows or Linux."); return; }
        var directory = Path.Combine(Path.GetTempPath(), $"vigiloom-text-log-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "active.log");
        var rotated = Path.Combine(directory, "old.log");
        try
        {
            var original = Encoding.UTF8.GetBytes(new string('a', 65_535) + "測試\n continuation\n");
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            var first = await TextFileSource.ReadAsync(path, null, TestContext.CancellationToken);
            Assert.AreEqual(TextFileReadStatus.Data, first.Status);
            Assert.HasCount(65_536, first.Data.ToArray());
            Assert.IsNotNull(first.NextCursor);
            var exact = await TextFileSource.ReadAsync(path, null, 7, TestContext.CancellationToken);
            Assert.AreEqual(7, exact.Data.Length);
            Assert.IsNotNull(exact.NextCursor);
            Assert.AreEqual(7L, exact.NextCursor.Offset);
            CollectionAssert.AreEqual(original[..7], exact.Data.ToArray());
            var afterExact = await TextFileSource.ReadAsync(path, exact.NextCursor, 9, TestContext.CancellationToken);
            CollectionAssert.AreEqual(original[7..16], afterExact.Data.ToArray());
            var second = await TextFileSource.ReadAsync(path, first.NextCursor, TestContext.CancellationToken);
            Assert.AreEqual(TextFileReadStatus.Data, second.Status);
            CollectionAssert.AreEqual(original, first.Data.ToArray().Concat(second.Data.ToArray()).ToArray());
            Assert.IsNotNull(second.NextCursor);
            Assert.AreEqual(TextFileReadStatus.End, (await TextFileSource.ReadAsync(path, second.NextCursor, TestContext.CancellationToken)).Status);

            await File.WriteAllTextAsync(path, "short\n", TestContext.CancellationToken);
            Assert.AreEqual(TextFileReadStatus.CursorInvalidated, (await TextFileSource.ReadAsync(path, second.NextCursor, TestContext.CancellationToken)).Status);
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            var restored = await TextFileSource.ReadAsync(path, first.NextCursor, TestContext.CancellationToken);
            Assert.AreEqual(TextFileReadStatus.Data, restored.Status);
            original[65_535] ^= 1;
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            Assert.AreEqual(TextFileReadStatus.CursorInvalidated, (await TextFileSource.ReadAsync(path, first.NextCursor, TestContext.CancellationToken)).Status);

            File.Move(path, rotated);
            await File.WriteAllBytesAsync(path, original, TestContext.CancellationToken);
            Assert.AreEqual(TextFileReadStatus.CursorInvalidated, (await TextFileSource.ReadAsync(path, first.NextCursor, TestContext.CancellationToken)).Status);
        }
        finally
        {
            File.Delete(path);
            File.Delete(rotated);
            Directory.Delete(directory);
        }
    }
}

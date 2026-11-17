using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class RecoveryStatusTests(TestContext context)
{
    [TestMethod]
    [DataRow(UpdatePhase.Prepared, "pending", 1)]
    [DataRow(UpdatePhase.Stopping, "pending", 1)]
    [DataRow(UpdatePhase.Switching, "pending", 1)]
    [DataRow(UpdatePhase.Starting, "pending", 1)]
    [DataRow(UpdatePhase.Verifying, "pending", 1)]
    [DataRow(UpdatePhase.RollingBack, "pending", 1)]
    [DataRow(UpdatePhase.Succeeded, "ready", 0)]
    [DataRow(UpdatePhase.RolledBack, "ready", 0)]
    [DataRow(UpdatePhase.RollbackFailed, "failed", 2)]
    [DataRow((UpdatePhase)999, "failed", 2)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RecoveryStatus_ReportsJournalPhaseWithoutChangingIt(UpdatePhase phase, string expected, int exitCode)
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"recovery-status-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var journal = new UpdateJournal(directory);
            await journal.WriteAsync(new UpdateState(Guid.CreateVersion7(), Path.Combine(directory, "old"), Path.Combine(directory, "new"),
                phase, now, [new(phase, now, null)]), context.CancellationToken);
            var path = Path.Combine(directory, "update.db");
            var before = SHA256.HashData(await File.ReadAllBytesAsync(path, context.CancellationToken));
            var result = await RunAsync(directory, context.CancellationToken);
            Assert.AreEqual(exitCode, result.ExitCode, result.Error);
            Assert.AreEqual(expected, result.Output.Trim());
            CollectionAssert.AreEqual(before, SHA256.HashData(await File.ReadAllBytesAsync(path, context.CancellationToken)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RecoveryStatus_DistinguishesMissingJournalFromMissingDirectoryOrCorruption()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"recovery-status-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.AreEqual((0, "ready"), await RunSummaryAsync(directory, context.CancellationToken));
            Assert.AreEqual((2, "failed"), await RunSummaryAsync(Path.Combine(directory, "missing"), context.CancellationToken));
            Assert.AreEqual((2, "failed"), await RunSummaryAsync(Path.GetPathRoot(directory)!, context.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(directory, "update.db"), "not a database", context.CancellationToken);
            Assert.AreEqual((2, "failed"), await RunSummaryAsync(directory, context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RecoveryStatus_InvalidArgumentsDoNotStartUpdaterHost()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"recovery-status-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var arguments in new[] { new[] { "recovery-status" }, new[] { "recovery-status", directory, "extra" } })
            {
                var result = await RunCommandAsync(arguments, context.CancellationToken, directory);
                Assert.AreEqual(2, result.ExitCode);
                Assert.Contains("Usage: recovery-status", result.Error);
                Assert.IsFalse(File.Exists(Path.Combine(directory, "updater.lock")));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RecoveryStatus_RetriesWhileJournalIsLocked()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"recovery-status-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await new UpdateJournal(directory).WriteAsync(new UpdateState(Guid.CreateVersion7(), Path.Combine(directory, "old"),
                Path.Combine(directory, "new"), UpdatePhase.Succeeded, now, [new(UpdatePhase.Succeeded, now, null)]), context.CancellationToken);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory, "update.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync(context.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync(context.CancellationToken);
            Assert.AreEqual((1, "pending"), await RunSummaryAsync(directory, context.CancellationToken));
            command.CommandText = "ROLLBACK";
            await command.ExecuteNonQueryAsync(context.CancellationToken);
            Assert.AreEqual((0, "ready"), await RunSummaryAsync(directory, context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output)> RunSummaryAsync(string directory, CancellationToken token)
    {
        var result = await RunAsync(directory, token);
        return (result.ExitCode, result.Output.Trim());
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAsync(string directory, CancellationToken token) =>
        RunCommandAsync(["recovery-status", directory], token);

    private static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string[] arguments, CancellationToken token, string? configuredRoot = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Vigiloom.Updater.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (configuredRoot is not null)
        {
            start.Environment["Vigiloom__Updater__InstallationRoot"] = configuredRoot;
            start.Environment["Vigiloom__Updater__StateDirectory"] = configuredRoot;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Updater.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await output, await errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
        }
    }
}

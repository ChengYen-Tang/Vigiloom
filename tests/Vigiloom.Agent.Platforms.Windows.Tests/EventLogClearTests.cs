using System.Diagnostics;
using System.Text;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Agent.Platforms.Windows.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

/// <summary>Exercises real channel clearing only on an ephemeral GitHub-hosted runner.</summary>
[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
public sealed class EventLogClearTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ClearedOwnedChannel_ReportsGapBeforeReadingNewEvent()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            Assert.Inconclusive("Requires an ephemeral GitHub-hosted Windows runner.");
            return;
        }

        // Windows classic logs can use their first eight characters as a file name.
        var channel = "V" + Guid.CreateVersion7().ToString("N")[^15..];
        var source = "VigiloomTest" + Guid.CreateVersion7().ToString("N");
        var creationAttempted = false;
        var childMayStillRun = false;
        Exception? failure = null;
        try
        {
            await RunAsync(channel, source, "preflight", context.CancellationToken);
            creationAttempted = true;
            await RunAsync(channel, source, "create", context.CancellationToken);
            await RunAsync(channel, source, "first", context.CancellationToken);
            var reader = new WindowsEventLogReader(channel);
            var first = await reader.ReadAsync(null, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Record, first.Status);
            Assert.IsNotNull(first.NextCursor);
            Assert.Contains("vigiloom-first", first.Record!.Content);

            await RunAsync(channel, source, "clear", context.CancellationToken);
            await RunAsync(channel, source, "second", context.CancellationToken);
            var gap = await reader.ReadAsync(first.NextCursor, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Gap, gap.Status);
            Assert.AreEqual(AgentLogGapReason.CursorLost, gap.Gap!.Reason);
            Assert.IsNull(gap.Gap.AffectedCount);
            Assert.AreEqual(NativeLogCursor.Restart, gap.NextCursor);
            var resumed = await reader.ReadAsync(gap.NextCursor, context.CancellationToken);
            Assert.AreEqual(LogReadStatus.Record, resumed.Status);
            Assert.Contains("vigiloom-second", resumed.Record!.Content);
        }
        catch (Exception error) { childMayStillRun = error is ChildStillRunningException; failure = error; }
        finally
        {
            if (creationAttempted && !childMayStillRun)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await RunAsync(channel, source, "delete", cleanup.Token);
                }
                catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task RunAsync(string channel, string source, string operation, CancellationToken token)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $name = $env:VIGILOOM_TEST_EVENT_CHANNEL
            $source = $env:VIGILOOM_TEST_EVENT_SOURCE
            if ($name -cnotmatch '^V[0-9a-f]{15}$') { throw 'Invalid test channel name.' }
            if ($source -cnotmatch '^VigiloomTest[0-9a-f]{32}$') { throw 'Invalid test source name.' }
            switch ($env:VIGILOOM_TEST_EVENT_OPERATION) {
              'preflight' {
                $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
                if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Runner is not an administrator.' }
                $prefix = $name.Substring(0, 8)
                if (Get-EventLog -List | Where-Object { $_.Log.Length -ge 8 -and $_.Log.Substring(0, 8) -ieq $prefix }) { throw 'Event log prefix already exists.' }
                if ([Diagnostics.EventLog]::SourceExists($source)) { throw 'Event source already exists.' }
              }
              'create' { New-EventLog -LogName $name -Source $source }
              'first' { Write-EventLog -LogName $name -Source $source -EntryType Information -EventId 101 -Message 'vigiloom-first' }
              'second' { Write-EventLog -LogName $name -Source $source -EntryType Information -EventId 102 -Message 'vigiloom-second' }
              'clear' { Clear-EventLog -LogName $name }
              'delete' {
                $sourceExists = [Diagnostics.EventLog]::SourceExists($source)
                $logExists = [Diagnostics.EventLog]::Exists($name)
                if (-not $sourceExists -and -not $logExists) { break }
                if (-not $sourceExists -or -not $logExists -or
                    -not [string]::Equals([Diagnostics.EventLog]::LogNameFromSourceName($source, '.'), $name, [StringComparison]::OrdinalIgnoreCase)) {
                  throw 'Cannot verify ownership of the partially created test log.'
                }
                Remove-EventLog -LogName $name
              }
              default { throw 'Invalid operation.' }
            }
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["VIGILOOM_TEST_EVENT_CHANNEL"] = channel;
        start.Environment["VIGILOOM_TEST_EVENT_SOURCE"] = source;
        start.Environment["VIGILOOM_TEST_EVENT_OPERATION"] = operation;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Windows PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Exception? failure = null;
        try { await process.WaitForExitAsync(token); }
        catch (Exception error) { failure = error; }
        var cleanupErrors = new List<Exception>();
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception error) { cleanupErrors.Add(error); }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanup.Token);
            }
            catch (Exception error) { cleanupErrors.Add(error); }
        }
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { cleanupErrors.Add(error); }
        if (!process.HasExited)
        {
            if (failure is not null) cleanupErrors.Insert(0, failure);
            throw new ChildStillRunningException($"Event log {operation} did not exit; owned log cleanup is deferred to runner disposal.", new AggregateException(cleanupErrors));
        }
        if (failure is null && process.HasExited && process.ExitCode != 0)
            failure = new InvalidOperationException($"Event log {operation} failed: {(stdout.IsCompletedSuccessfully ? stdout.Result : "")} {(stderr.IsCompletedSuccessfully ? stderr.Result : "")}");
        if (cleanupErrors.Count > 0)
        {
            if (failure is not null) cleanupErrors.Insert(0, failure);
            throw new AggregateException(cleanupErrors);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ChildStillRunningException(string message, Exception inner) : Exception(message, inner);
}

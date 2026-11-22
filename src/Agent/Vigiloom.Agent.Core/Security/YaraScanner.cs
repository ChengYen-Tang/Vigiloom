using System.Diagnostics;
using System.Text;

namespace Vigiloom.Agent.Core.Security;

public enum YaraScanStatus { Completed, FileTooLarge, TimedOut, Unavailable, Failed }

public sealed record YaraScanResult(YaraScanStatus Status, string Output, string Error, bool OutputTruncated);

/// <summary>Runs the installed YARA engine on one explicitly selected file.</summary>
public static class YaraScanner
{
    private const int OutputLimit = 64 * 1024;

    public static async Task<YaraScanResult> ScanAsync(string executable, string rules, string file,
        long maximumBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (maximumBytes <= 0 || timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var targetPath = Path.GetFullPath(file);
            var rulePath = Path.GetFullPath(rules);
            var target = new FileInfo(targetPath);
            if (!target.Exists) return new(YaraScanStatus.Unavailable, "", "File not found.", false);
            if (target.Length > maximumBytes) return new(YaraScanStatus.FileTooLarge, "", "", false);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--no-follow-symlinks");
            start.ArgumentList.Add("--timeout=" + Math.Ceiling(timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(rulePath);
            start.ArgumentList.Add(targetPath);
            using var process = Process.Start(start);
            if (process is null) return new(YaraScanStatus.Unavailable, "", "YARA could not start.", false);
            var output = ReadAsync(process.StandardOutput);
            var error = ReadAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                return new(YaraScanStatus.TimedOut, "", "", false);
            }
            var stdout = await output;
            var stderr = await error;
            return new(process.ExitCode == 0 ? YaraScanStatus.Completed : YaraScanStatus.Failed,
                stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(YaraScanStatus.Unavailable, "", exception.Message, false);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        var truncated = false;
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        {
            var remaining = OutputLimit - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        return (result.ToString(), truncated);
    }
}

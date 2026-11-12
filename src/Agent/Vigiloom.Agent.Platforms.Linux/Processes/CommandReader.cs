using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Processes;

/// <summary>Runs a fixed executable without a shell and bounds its output and lifetime.</summary>
internal static class CommandReader
{
    private const int MaximumCharacters = 4 * 1024 * 1024;

    internal static async Task<(InventoryReadStatus Status, string Output)> ReadAsync(string path, string[] arguments, CancellationToken token)
    {
        if (!File.Exists(path)) return (InventoryReadStatus.Unsupported, "");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        start.Environment["SYSTEMD_COLORS"] = "0";
        start.Environment["SYSTEMD_PAGER"] = "cat";
        using var process = new Process { StartInfo = start };
        var started = false;
        Task? errors = null;
        try
        {
            if (!process.Start()) return (InventoryReadStatus.Error, "");
            started = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var output = new StringBuilder();
            var buffer = new char[4096];
            bool truncated = false;
            while (true)
            {
                var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token);
                if (count == 0) break;
                if (count > MaximumCharacters - output.Length) { truncated = true; break; }
                output.Append(buffer, 0, count);
            }
            if (truncated) Stop(process);
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            if (!truncated && process.ExitCode != 0) return (InventoryReadStatus.Error, "");
            return (truncated ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, output.ToString());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (InventoryReadStatus.Error, ""); }
        catch (UnauthorizedAccessException) { return (InventoryReadStatus.PermissionDenied, ""); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 13) { return (InventoryReadStatus.PermissionDenied, ""); }
        catch (Exception ex) when (ex is IOException or Win32Exception or DecoderFallbackException) { return (InventoryReadStatus.Error, ""); }
        finally
        {
            if (started)
            {
                Stop(process);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
                if (errors is not null)
                {
                    try { await errors.WaitAsync(cleanup.Token); }
                    catch (OperationCanceledException) when (errors.IsCanceled) { }
                    catch (IOException) when (errors.IsFaulted) { }
                }
            }
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}

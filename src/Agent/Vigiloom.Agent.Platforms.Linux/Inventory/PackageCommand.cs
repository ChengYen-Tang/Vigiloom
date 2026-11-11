using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

/// <summary>Runs a bounded package query without a shell and consumes its output incrementally.</summary>
internal static class PackageCommand
{
    private const int MaximumEntries = 20_000;
    private const int MaximumTextLength = 2048;

    [SupportedOSPlatform("linux")]
    public static async Task<InventoryReadResult<InstalledSoftware>> ReadAsync(string command, IReadOnlyList<string> arguments,
        Func<string, (LineStatus Status, InstalledSoftware? Item)> parse, CancellationToken token)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = start };
        Task? errorDrain = null;
        var started = false;
        try
        {
            if (!process.Start()) return new(InventoryReadStatus.Error, []);
            started = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var items = new List<InstalledSoftware>();
            var (partial, exceeded) = await ReadRowsAsync(process.StandardOutput, items, parse, deadline.Token);
            if (exceeded) StopIfRunning(process);
            await process.WaitForExitAsync(deadline.Token);
            await errorDrain;
            if (!exceeded && process.ExitCode != 0) return new(InventoryReadStatus.Error, []);
            return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, items);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(InventoryReadStatus.Error, []);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // A present executable may still fail with ENOENT when its interpreter is missing.
            return new(File.Exists(command) ? InventoryReadStatus.Error : InventoryReadStatus.Unsupported, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(ex is UnauthorizedAccessException or System.ComponentModel.Win32Exception { NativeErrorCode: 13 }
                ? InventoryReadStatus.PermissionDenied : InventoryReadStatus.Error, []);
        }
        catch (DecoderFallbackException)
        {
            return new(InventoryReadStatus.Error, []);
        }
        finally
        {
            if (started)
            {
                StopIfRunning(process);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
                if (errorDrain is not null)
                {
                    try { await errorDrain.WaitAsync(cleanup.Token); }
                    catch (OperationCanceledException) when (errorDrain.IsCanceled) { }
                    catch (IOException) when (errorDrain.IsFaulted) { }
                }
            }
        }
    }

    private static void StopIfRunning(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    internal static async Task<(bool Partial, bool Exceeded)> ReadRowsAsync(TextReader reader, List<InstalledSoftware> items,
        Func<string, (LineStatus Status, InstalledSoftware? Item)> parse, CancellationToken token)
    {
        const int maximumLine = 5 * MaximumTextLength + 8;
        const int maximumOutput = 32 * 1024 * 1024;
        const int maximumRetainedCharacters = 4_000_000;
        var buffer = new char[4096];
        var line = new StringBuilder();
        var partial = false;
        var oversized = false;
        var total = 0;
        var retained = 0;
        void FinishLine()
        {
            if (oversized) partial = true;
            else
            {
                var (status, item) = parse(line.ToString());
                if (status == LineStatus.Invalid) partial = true;
                else if (status == LineStatus.Installed && item is not null)
                {
                    var size = item.SourceKey.Length + item.Name.Length + (item.Version?.Length ?? 0) +
                        (item.Supplier?.Length ?? 0) + (item.Architecture?.Length ?? 0);
                    if (items.Count >= MaximumEntries || size > maximumRetainedCharacters - retained) partial = true;
                    else { items.Add(item); retained += size; }
                }
            }
            line.Clear();
            oversized = false;
        }
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            total += read;
            if (total > maximumOutput) return (true, true);
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n') FinishLine();
                else if (line.Length < maximumLine && !oversized) line.Append(c);
                else oversized = true;
            }
        }
        if (line.Length > 0 || oversized) FinishLine();
        return (partial, false);
    }

    internal enum LineStatus { Installed, NotInstalled, Invalid }
}

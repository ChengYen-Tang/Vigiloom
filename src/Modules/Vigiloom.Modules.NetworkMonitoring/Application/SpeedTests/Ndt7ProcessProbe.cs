using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vigiloom.Contracts.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed record SpeedMeasurement(SpeedTestState State, string? Server = null, string? City = null, string? Country = null,
    double? DownloadMbps = null, double? UploadMbps = null, double? LatencyMilliseconds = null, long? Bytes = null,
    double? Latitude = null, double? Longitude = null, string? DiagnosticCode = null);
public interface ISpeedProbe
{
    Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token);
}

public sealed class Ndt7ProcessProbe : ISpeedProbe
{
    public async Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(SpeedTestState.Cancelled, DiagnosticCode: "cancelled_before_start");
        var started = false;
        var path = Path.Combine(AppContext.BaseDirectory, "tools", OperatingSystem.IsWindows() ? "vigiloom-ndt7.exe" : "vigiloom-ndt7");
        if (!File.Exists(path)) return new(SpeedTestState.Unavailable, DiagnosticCode: "client_missing");
        using var process = new Process { StartInfo = new(path)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory
        } };
        try
        {
            if (!process.Start()) return new(SpeedTestState.Unavailable, DiagnosticCode: "client_start_failed");
            started = true;
            var stdout = ReadBoundedAsync(process.StandardOutput, 8192);
            var stderr = ReadBoundedAsync(process.StandardError, 8192);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { MaxBytes = (long)lease.MaxMiB * 1024 * 1024, lease.TimeoutSeconds }));
            await process.StandardInput.FlushAsync();
            using var stop = token.Register(() => { try { process.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { } });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(lease.TimeoutSeconds + 5));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                process.StandardInput.Close();
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
            var output = await stdout; await stderr;
            if (process.ExitCode != 0 || output is null) return new(token.IsCancellationRequested ? SpeedTestState.Cancelled : SpeedTestState.Failed, DiagnosticCode: "client_exit");
            var result = JsonSerializer.Deserialize<ClientResult>(output);
            if (result is null || !Valid(result, lease)) return new(SpeedTestState.Failed, DiagnosticCode: "invalid_client_result");
            var state = token.IsCancellationRequested ? SpeedTestState.Cancelled : result.Status switch
            {
                "Success" => SpeedTestState.Success, "Partial" => SpeedTestState.Partial, "BudgetExceeded" => SpeedTestState.BudgetExceeded,
                "Timeout" => SpeedTestState.TimedOut, "Cancelled" => SpeedTestState.Cancelled, "NoServer" => SpeedTestState.NoServer,
                _ => SpeedTestState.Failed
            };
            return new(state, result.Server, result.City, result.Country, result.DownloadMbps, result.UploadMbps,
                result.LatencyMilliseconds, result.Server is null && result.Bytes == 0 ? null : result.Bytes, result.Latitude, result.Longitude, state == SpeedTestState.Success ? null : result.Status);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            return new(SpeedTestState.Unavailable, DiagnosticCode: "client_io_failed");
        }
    }
    private static bool Valid(ClientResult result, SpeedLease lease) =>
        result.Bytes is >= 0 && result.Bytes <= (long)lease.MaxMiB * 1024 * 1024 &&
        new[] { result.DownloadMbps, result.UploadMbps, result.LatencyMilliseconds }.All(x => x is null || double.IsFinite(x.Value) && x >= 0) &&
        result.Server?.Length is not > 253 && result.City?.Length is not > 100 && result.Country?.Length is not > 100 &&
        result.Latitude is null or >= -90 and <= 90 && result.Longitude is null or >= -180 and <= 180 &&
        (result.Status != "Success" || result.DownloadMbps > 0 && result.UploadMbps > 0);
    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder(); var buffer = new char[1024]; var overflow = false;
        while (await reader.ReadAsync(buffer) is var count && count != 0)
        {
            if (text.Length + count <= limit) text.Append(buffer, 0, count); else overflow = true;
        }
        return overflow ? null : text.ToString();
    }
    private sealed record ClientResult(string Status, string? Server, string? City, string? Country, double? DownloadMbps,
        double? UploadMbps, double? LatencyMilliseconds, long Bytes, double? Latitude, double? Longitude);
}

using System.Diagnostics;
using System.Text.Json;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.SpeedLab;

internal static class Program
{
    public static async Task Main()
    {
        using var timer = TimerPrecision.Request();
        var profiles = new[]
        {
            new Profile(890, 244, 0),
            new Profile(890, 244, 50),
            new Profile(10, 5, 50)
        };

        foreach (var profile in profiles)
        {
            for (var trial = 1; trial <= 2; trial++)
            {
                var handler = new ControlledNetwork(profile);
                var probe = new SpeedtestNetProbe(new OfflineHandlerFactory(handler));
                var elapsed = Stopwatch.StartNew();
                var result = await probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2048, 60), CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    profile, trial, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    state = result.State.ToString(), result.DownloadMbps, result.UploadMbps,
                    result.Bytes, result.DiagnosticCode,
                    downloadReferenceMbps = handler.Download.ObservedMbps,
                    uploadReferenceMbps = handler.Upload.ObservedMbps,
                    downloadBytes = handler.Download.Bytes, uploadBytes = handler.Upload.Bytes
                }));
            }
        }
    }

    private sealed class OfflineHandlerFactory(ControlledNetwork handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }
}

internal sealed record Profile(int DownloadMbps, int UploadMbps, int ExtraRequestDelayMilliseconds);

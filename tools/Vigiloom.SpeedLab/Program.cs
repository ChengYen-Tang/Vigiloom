using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.SpeedLab;

internal static class Program
{
    private const int MaxMiB = 4096;
    private const int TimeoutSeconds = 60;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("Usage: dotnet run --project tools/Vigiloom.SpeedLab --configuration Release -- [--servers ID,ID,... | --server-id ID | --multi | --offline | --help]");
            Console.WriteLine($"Default: one real Speedtest.net measurement via HTTP (up to {MaxMiB} MiB of content traffic, {TimeoutSeconds} seconds).");
            Console.WriteLine("--offline: run the six synthetic bandwidth trials without network access.");
            Console.WriteLine("--server-id ID: measure only the specified Speedtest server ID.");
            Console.WriteLine("--multi: experimental adaptive download from up to four servers.");
            Console.WriteLine("--servers ID,ID,...: fix one to four servers in order; the first is primary.");
            return 0;
        }
        int? serverId = null;
        int[]? fixedServers = null;
        if (args is ["--server-id", var serverIdText] &&
            int.TryParse(serverIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0)
            serverId = parsedId;
        else if (args is ["--servers", var ids] && TryParseServers(ids, out fixedServers)) { }
        else if (args.Length != 0 && args is not ["--offline"] and not ["--multi"])
        {
            Console.Error.WriteLine("Unknown arguments. Use --help for usage.");
            return 2;
        }

        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            return args is ["--offline"]
                ? await RunOfflineAsync(stopping.Token)
                : await RunLiveAsync(serverId, serverId is null, fixedServers, stopping.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static bool TryParseServers(string value, out int[]? result)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        var ids = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || ids.Contains(id))
            {
                result = null;
                return false;
            }
            ids.Add(id);
        }
        result = ids.Count is >= 1 and <= 4 ? [.. ids] : null;
        return result is not null;
    }

    private static async Task<int> RunLiveAsync(int? serverId, bool multi, int[]? fixedServers, CancellationToken token)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(SpeedtestNetProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(SpeedtestNetProbe.CreatePrimaryHandler);
        using var provider = services.BuildServiceProvider();
        var probe = new SpeedtestNetProbe(provider.GetRequiredService<IHttpMessageHandlerFactory>());

        Console.Error.WriteLine($"Measuring against real Speedtest.net servers via HTTP (limit: {MaxMiB} MiB, {TimeoutSeconds} seconds). Press Ctrl+C to cancel.");
        var startedAt = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var result = await probe.MeasureAsync(NewLease(), serverId, token, multiServerDownload: multi,
            fixedDownloadServerIds: fixedServers);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "live", source = "Speedtest.net", client = "Vigiloom HTTP / NetPace",
            startedAt, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            maxMiB = MaxMiB, timeoutSeconds = TimeoutSeconds,
            requestedServerId = serverId, requestedServerIds = fixedServers,
            state = result.State.ToString(), result.Server, result.City, result.Country,
            result.DownloadMbps, result.UploadMbps, result.LatencyMilliseconds,
            result.Bytes, result.Latitude, result.Longitude, result.DiagnosticCode
        }));
        return ExitCode(result);
    }

    private static SpeedLease NewLease() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), MaxMiB, TimeoutSeconds);

    private static int ExitCode(SpeedMeasurement result) => result.State switch
    {
        SpeedTestState.Success => 0,
        SpeedTestState.Cancelled => 130,
        _ => 1
    };

    private static async Task<int> RunOfflineAsync(CancellationToken token)
    {
        using var timer = TimerPrecision.Request();
        var exitCode = 0;
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
                if (token.IsCancellationRequested) return 130;
                using var handler = new ControlledNetwork(profile);
                var probe = new SpeedtestNetProbe(new OfflineHandlerFactory(handler));
                var elapsed = Stopwatch.StartNew();
                var result = await probe.MeasureAsync(NewLease(), token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    mode = "offline", profile, trial, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    state = result.State.ToString(), result.DownloadMbps, result.UploadMbps,
                    result.Bytes, result.DiagnosticCode,
                    downloadReferenceMbps = handler.Download.ObservedMbps,
                    uploadReferenceMbps = handler.Upload.ObservedMbps,
                    downloadBytes = handler.Download.Bytes, uploadBytes = handler.Upload.Bytes
                }));
                if (result.State == SpeedTestState.Cancelled) return 130;
                if (ExitCode(result) != 0) exitCode = 1;
            }
        }
        return exitCode;
    }

    private sealed class OfflineHandlerFactory(ControlledNetwork handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }
}

internal sealed record Profile(int DownloadMbps, int UploadMbps, int ExtraRequestDelayMilliseconds);

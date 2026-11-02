using NetPace.Core;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

// Supplies deterministic synthetic upload samples for tests of the probe's
// state handling; real uploads use ContinuousSpeedUpload.
internal static class SpeedProbeTestFactory
{
    internal static SpeedtestNetProbe Create(ISpeedTestService service, int targetMilliseconds = SpeedtestNetProbe.DefaultTargetMilliseconds)
    {
        return new(service, targetMilliseconds, async (server, lease, update, token) =>
        {
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
            phase.CancelAfter(TimeSpan.FromSeconds(SpeedtestNetProbe.PhaseDeadlineSeconds(lease.TimeoutSeconds)));
            var samples = new List<(long Bytes, long Milliseconds)>();
            long bytes = 0, elapsed = 0;
            var failures = 0;
            var limit = Math.Max(1, lease.MaxMiB / 2) * 1024L * 1024L;
            for (var batch = 1; batch <= 256 && elapsed < targetMilliseconds && bytes < limit; batch++)
            {
                SpeedTestResult sample;
                try { sample = await service.GetUploadSpeedAsync(server, phase.Token); }
                catch (OperationCanceledException) when (phase.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    return (bytes, elapsed, failures + 1, true);
                }
                var transferred = Math.Max(0, sample.BytesProcessed);
                var duration = Math.Max(0, sample.ElapsedMilliseconds);
                bytes += transferred;
                elapsed += duration;
                failures += sample.RequestsFailed;
                if (sample.RequestsSucceeded == 0 || sample.ElapsedMilliseconds < 0 || transferred == 0)
                {
                    failures++;
                    break;
                }
                if (duration > 0)
                {
                    samples.Add((transferred, duration));
                    update(SpeedtestNetProbe.CalculateTrimmedRate(samples));
                }
            }
            return (bytes, elapsed, failures, false);
        });
    }
}

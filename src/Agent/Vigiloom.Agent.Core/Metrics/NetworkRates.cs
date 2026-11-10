using System.Net.NetworkInformation;

namespace Vigiloom.Agent.Core.Metrics;

public sealed record NetworkCounters(string Id, string Name, ulong ReceivedBytes, ulong SentBytes, ulong ReceivedErrors, ulong SentErrors);
public sealed record NetworkRate(string Id, string Name, double? ReceivedBytesPerSecond, double? SentBytesPerSecond,
    double? ReceivedErrorsPerSecond, double? SentErrorsPerSecond);
public sealed record NetworkSample(IReadOnlyList<NetworkRate> Interfaces, int UnavailableInterfaces, bool Truncated);

/// <summary>Samples OS interface counters; one instance must be used by a single collector.</summary>
public sealed class NetworkSampler(TimeProvider clock)
{
    public const int MaximumInterfaces = 256;
    private readonly NetworkRateTracker tracker = new(clock);

    public void Reset() => tracker.Reset();

    public NetworkSample Sample(TimeSpan maximumInterval)
    {
        var devices = NetworkInterface.GetAllNetworkInterfaces();
        Array.Sort(devices, static (a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        var counters = new List<NetworkCounters>(Math.Min(devices.Length, MaximumInterfaces));
        var unavailable = 0;
        for (var index = 0; index < devices.Length && index < MaximumInterfaces; index++)
        {
            var device = devices[index];
            try
            {
                var statistics = device.GetIPStatistics();
                var received = statistics.BytesReceived;
                var sent = statistics.BytesSent;
                var receiveErrors = statistics.IncomingPacketsWithErrors;
                var sendErrors = statistics.OutgoingPacketsWithErrors;
                if (received < 0 || sent < 0 || receiveErrors < 0 || sendErrors < 0 || string.IsNullOrWhiteSpace(device.Id))
                {
                    unavailable++;
                    continue;
                }
                counters.Add(new(device.Id, device.Name, (ulong)received, (ulong)sent, (ulong)receiveErrors, (ulong)sendErrors));
            }
            catch (NetworkInformationException) { unavailable++; }
            catch (PlatformNotSupportedException) { unavailable++; }
        }
        return new(tracker.Update(counters, clock.GetTimestamp(), maximumInterval), unavailable, devices.Length > MaximumInterfaces);
    }
}

/// <summary>Computes rates only for matching interfaces in consecutive samples.</summary>
public sealed class NetworkRateTracker(TimeProvider clock)
{
    private Dictionary<string, NetworkCounters> previous = new(StringComparer.Ordinal);
    private long? previousTimestamp;

    public void Reset()
    {
        previous.Clear();
        previousTimestamp = null;
    }

    public IReadOnlyList<NetworkRate> Update(IReadOnlyList<NetworkCounters> current, long timestamp, TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        var next = new Dictionary<string, NetworkCounters>(current.Count, StringComparer.Ordinal);
        var result = new List<NetworkRate>(current.Count);
        var elapsed = previousTimestamp is { } prior ? clock.GetElapsedTime(prior, timestamp) : TimeSpan.Zero;
        foreach (var item in current)
        {
            if (!next.TryAdd(item.Id, item)) throw new ArgumentException("Duplicate network interface ID.", nameof(current));
            previous.TryGetValue(item.Id, out var old);
            result.Add(new(item.Id, item.Name,
                CounterRate.PerSecond(old?.ReceivedBytes, item.ReceivedBytes, elapsed, maximumInterval),
                CounterRate.PerSecond(old?.SentBytes, item.SentBytes, elapsed, maximumInterval),
                CounterRate.PerSecond(old?.ReceivedErrors, item.ReceivedErrors, elapsed, maximumInterval),
                CounterRate.PerSecond(old?.SentErrors, item.SentErrors, elapsed, maximumInterval)));
        }
        previous = next;
        previousTimestamp = timestamp;
        return result;
    }
}

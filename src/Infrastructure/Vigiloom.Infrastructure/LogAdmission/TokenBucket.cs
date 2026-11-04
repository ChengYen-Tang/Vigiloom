namespace Vigiloom.Infrastructure.LogAdmission;

/// <summary>Pure token arithmetic shared by durable telemetry quota implementations.</summary>
internal static class TokenBucket
{
    internal const long Unit = 1_000_000;
    internal sealed record State(long Tokens, DateTimeOffset LastRefill, byte[] PolicyVersion, bool Exists);

    internal static long RetryMicroseconds(long deficit, int rate, DateTimeOffset last, DateTimeOffset now) =>
        deficit <= 0 ? 0 : WaitMicroseconds(deficit, rate) + (last > now ? ((last - now).Ticks + 9) / 10 : 0);

    internal static State Refill(State? existing, int capacity, int rate, byte[] version, DateTimeOffset policyAt, DateTimeOffset now)
    {
        var max = capacity * Unit;
        if (existing is null) return new(max, now, version, false);
        var tokens = Math.Min(existing.Tokens, max);
        var last = existing.LastRefill;
        if (!existing.PolicyVersion.AsSpan().SequenceEqual(version) && policyAt > last) last = policyAt;
        if (now > last)
        {
            var elapsed = (now - last).Ticks / 10;
            var missing = max - tokens;
            tokens = elapsed >= WaitMicroseconds(missing, rate) ? max : tokens + elapsed * rate;
            last = now;
        }
        return new(tokens, last, version, true);
    }

    private static long WaitMicroseconds(long deficit, int rate) => deficit <= 0 ? 0 : (deficit + rate - 1) / rate;
}

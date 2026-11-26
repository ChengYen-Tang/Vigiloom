using System.Threading.RateLimiting;

namespace Vigiloom.HostMonitoring;

public sealed class AgentHeartbeatLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter;
    private readonly ConcurrencyLimiter concurrent;

    public AgentHeartbeatLimits(IConfiguration configuration)
    {
        var count = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:RequestsPerMinute", 120), 1, 10000);
        concurrent = new(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:MaxConcurrentRequests", 128), 1, 1024),
            QueueLimit = Math.Clamp(configuration.GetValue("Vigiloom:Agent:Transport:MaxQueuedRequests", 512), 0, 4096),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        limiter = PartitionedRateLimiter.Create<string, string>(id => RateLimitPartition.GetFixedWindowLimiter(id, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = count, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    public ValueTask<RateLimitLease> AcquireAsync(string id, CancellationToken token) => limiter.AcquireAsync(id, 1, token);
    public ValueTask<RateLimitLease> AcquireSlotAsync(CancellationToken token) => concurrent.AcquireAsync(1, token);
    public void Dispose() { limiter.Dispose(); concurrent.Dispose(); }
}

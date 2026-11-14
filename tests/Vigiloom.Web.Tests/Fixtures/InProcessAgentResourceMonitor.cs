using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Hosting.Metrics;

namespace Vigiloom.Web.Tests.Fixtures;

// The Agent runs inside the web test host, so process counters include unrelated work.
internal sealed class InProcessAgentResourceMonitor : IMetricResourceMonitor
{
    public long ReadWorkingSetBytes() => 1;
    public TimeSpan ReadCpuTime() => TimeSpan.Zero;
    public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) => ValueTask.FromResult(new ProcessIoSnapshot(0, 0));
    public void Dispose() { }
}

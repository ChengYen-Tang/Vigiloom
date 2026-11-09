using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

internal static class SpeedRetentionEligibility
{
    public static IQueryable<SpeedRun> Apply(IQueryable<SpeedRun> runs, DateTimeOffset cutoff, DateTimeOffset protectedSince) =>
        runs.Where(x => x.RequestedAt < cutoff && x.State != SpeedTestState.Queued && x.State != SpeedTestState.Running &&
            (x.StartedAt ?? x.RequestedAt) <= protectedSince);
}

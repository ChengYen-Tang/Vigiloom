using System.Net.Http.Json;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling;

namespace Vigiloom.Web.Diagnostics;

internal sealed class JobOverview(SchedulingOverview local, IConfiguration configuration, IServiceProvider services)
{
    public async Task<WorkOverview> GetAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])) return await local.GetAsync(token);
        return await services.GetRequiredService<RemoteJobOverview>().GetAsync(token);
    }
}

internal sealed class RemoteJobOverview(HttpClient client)
{
    public async Task<WorkOverview> GetAsync(CancellationToken token) =>
        await client.GetFromJsonAsync<WorkOverview>("internal/v1/diagnostics/jobs", token)
        ?? throw new InvalidOperationException("Operations service returned an empty job overview.");

    public async Task<SchedulingHeartbeats> SchedulerAsync(CancellationToken token)
    {
        var reading = await client.GetFromJsonAsync<SchedulerHeartbeatsInfo>("internal/v1/diagnostics/scheduler", token)
            ?? throw new InvalidOperationException("Operations service returned empty scheduler diagnostics.");
        return new(reading.ObservedAt, reading.Values);
    }

    public async Task<Vigiloom.Modules.AlertingOperations.Application.WorkerDiagnosticsSnapshot> WorkersAsync(CancellationToken token)
    {
        var reading = await client.GetFromJsonAsync<WorkerDiagnosticsInfo>("internal/v1/diagnostics/workers", token)
            ?? throw new InvalidOperationException("Operations service returned empty worker diagnostics.");
        return new(reading.ObservedAt, reading.Workers.Select(item => new Vigiloom.Modules.AlertingOperations.Application.WorkerDiagnostic(
            item.BootId, item.ObservedAt, item.LeaseUntil, new(Map(item.Ping), Map(item.Speed)))).ToArray());
    }

    private static Vigiloom.Modules.AlertingOperations.Application.ScannerObservation Map(ScannerDiagnosticInfo value) =>
        new(value.LastSuccess, value.LastFailure, value.ConsecutiveFailures,
            (Vigiloom.Modules.AlertingOperations.Application.ScannerFailureKind)value.FailureKind);
}

using Vigiloom.Contracts.Operations;

namespace Vigiloom.Network;

/// <summary>Keeps the latest scanner outcomes for this network service instance.</summary>
internal sealed class NetworkDiagnostics(TimeProvider clock)
{
    private readonly Lock gate = new();
    private ScannerDiagnosticInfo ping = new(null, null, 0, ScannerDiagnosticFailure.None);
    private ScannerDiagnosticInfo speed = new(null, null, 0, ScannerDiagnosticFailure.None);
    public Guid BootId { get; } = Guid.CreateVersion7();

    public (ScannerDiagnosticInfo Ping, ScannerDiagnosticInfo Speed) Snapshot()
    {
        lock (gate) return (ping, speed);
    }

    public void Record(bool isPing, Exception? error)
    {
        lock (gate)
        {
            var current = isPing ? ping : speed;
            var kind = error switch
            {
                null => ScannerDiagnosticFailure.None,
                Microsoft.Data.SqlClient.SqlException => ScannerDiagnosticFailure.Database,
                TimeoutException or OperationCanceledException => ScannerDiagnosticFailure.Timeout,
                _ => ScannerDiagnosticFailure.Other
            };
            var next = error is null
                ? current with { LastSuccess = clock.GetUtcNow(), ConsecutiveFailures = 0, FailureKind = kind }
                : current with { LastFailure = clock.GetUtcNow(), ConsecutiveFailures = current.ConsecutiveFailures == int.MaxValue ? int.MaxValue : current.ConsecutiveFailures + 1, FailureKind = kind };
            if (isPing) ping = next; else speed = next;
        }
    }
}

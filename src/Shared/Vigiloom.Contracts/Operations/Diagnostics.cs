namespace Vigiloom.Contracts.Operations;

public sealed record SchedulerHeartbeatsInfo(DateTimeOffset ObservedAt, IReadOnlyList<DateTimeOffset?> Values);
public enum ScannerDiagnosticFailure : short { None, Database, Timeout, Other }
public sealed record ScannerDiagnosticInfo(DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, int ConsecutiveFailures, ScannerDiagnosticFailure FailureKind);
public sealed record WorkerDiagnosticInfo(Guid BootId, DateTimeOffset? ObservedAt, DateTimeOffset? LeaseUntil,
    ScannerDiagnosticInfo Ping, ScannerDiagnosticInfo Speed);
public sealed record WorkerDiagnosticsInfo(DateTimeOffset ObservedAt, IReadOnlyList<WorkerDiagnosticInfo> Workers);

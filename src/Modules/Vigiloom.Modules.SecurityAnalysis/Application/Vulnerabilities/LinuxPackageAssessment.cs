namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum LinuxEvaluationStatus { Ready, InventoryUnavailable, SourceUnavailable, Incomplete, Changed }
internal enum LinuxFindingReason { VendorRange, NoAdvisoryEvidence, MissingPackageIdentity, AmbiguousVendorEvidence }
internal sealed record LinuxPackageFinding(string PackageKey, string? AdvisoryId, MatchState State, PackageOrigin Origin, LinuxFindingReason Reason,
    string? SourcePackage, string? SourceVersion, string? BinaryVersion, string? Architecture, UbuntuFixStatus? FixStatus = null, string? FixedVersion = null,
    string? UbuntuPriority = null);
internal sealed record LinuxPackageAssessment(LinuxEvaluationStatus Status, LinuxInventoryStatus? InventoryStatus = null,
    Guid? RevisionId = null, Guid? HostSnapshotId = null, Guid? PackageSnapshotId = null,
    IReadOnlyList<LinuxPackageFinding>? Findings = null, Guid? SourceId = null, string? Ecosystem = null,
    byte[]? SourceVersion = null, byte[]? ActivationVersion = null, Guid? RunId = null, bool IsDebian = false);

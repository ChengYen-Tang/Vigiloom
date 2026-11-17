using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Updates;

public sealed record UpdateRequestResult(CurrentAssignmentStatus Assignment, UpdateState? State = null);

/// <summary>Resolves a local request, acquires its publisher-signed package and performs the update.</summary>
/// <remarks>All settings are installer-owned. Requests are processed sequentially after recovery.</remarks>
public sealed class UpdateRequestProcessor(CentralAssignmentClient central, CentralPackageAcquirer packages,
    UpdateTransaction transaction, IUpdateJournal journal, IAgentUpdateRuntime runtime, Uri server, Guid deviceId,
    string installationRoot, string runtimeIdentifier, string updaterVersion, UpdateResultDelivery? delivery = null)
{
    public async Task<UpdateRequestResult> ProcessAsync(UpdateHandoff request, CancellationToken token)
    {
        var assignment = await central.GetAsync(server, deviceId, request, token);
        if (assignment.Status is CurrentAssignmentStatus.Available or CurrentAssignmentStatus.Changed && delivery is not null)
            await delivery.FlushAsync(server, request.Credential, token);
        if (assignment is not { Status: CurrentAssignmentStatus.Available, Target: not null })
            return new(assignment.Status);

        var existing = await journal.ReadAsync(token);
        if (existing?.Id == request.UpdateId && (existing.Phase is UpdatePhase.Succeeded or UpdatePhase.RollbackFailed ||
            existing.Phase == UpdatePhase.RolledBack && !existing.RetryAllowed))
            return new(CurrentAssignmentStatus.Available, existing);
        if (existing is not null && existing.Phase is not (UpdatePhase.Succeeded or UpdatePhase.RolledBack))
            throw new InvalidOperationException("The previous update must be recovered first.");

        if (assignment.Target.RuntimeIdentifier != runtimeIdentifier)
            throw new InvalidDataException("The assigned package targets a different platform.");
        var previous = await runtime.GetSelectedDirectoryAsync(token);
        var currentVersion = InstalledAgentVersion.Read(previous);
        var target = assignment.Target;
        var provenance = new UpdateProvenance(Guid.ParseExact(request.Credential.AsSpan(4, 36), "D"), target.PackageId, target.ManifestSha256);
        await using var package = await packages.AcquireAsync(target.ManifestSha256, target.PackageVersion, runtimeIdentifier,
            updaterVersion, currentVersion, request.Credential, token);
        var result = await transaction.ApplyAsync(server, request.Credential, target, package.Manifest, package.Archive,
            installationRoot, previous, runtimeIdentifier, updaterVersion, currentVersion, token, provenance);
        if (delivery is not null) await delivery.FlushAsync(server, request.Credential, token);
        return new(CurrentAssignmentStatus.Available, result);
    }
}

using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Updates;

public sealed record UpdateRequestResult(CurrentAssignmentStatus Assignment, UpdateState? State = null);

/// <summary>Resolves a local request, acquires its publisher-signed package and performs the update.</summary>
/// <remarks>All settings are installer-owned. Requests are processed sequentially after recovery.</remarks>
public sealed class UpdateRequestProcessor(CentralAssignmentClient central, CentralPackageAcquirer packages,
    UpdateTransaction transaction, IUpdateJournal journal, IAgentUpdateRuntime runtime, Uri server, Guid deviceId,
    string installationRoot, string runtimeIdentifier, string updaterVersion, UpdateResultDelivery? delivery = null,
    Func<string, CancellationToken, Task>? retainCredential = null)
{
    public async Task<UpdateRequestResult> ProcessAsync(UpdateHandoff request, CancellationToken token)
    {
        var assignment = await central.GetAsync(server, deviceId, request, token);
        if (assignment.Status is CurrentAssignmentStatus.Available or CurrentAssignmentStatus.Changed && retainCredential is not null)
            await retainCredential(request.Credential, token);
        if (assignment.Status is CurrentAssignmentStatus.Available or CurrentAssignmentStatus.Changed && delivery is not null)
            await delivery.FlushAsync(server, request.Credential, token);
        if (assignment is not { Status: CurrentAssignmentStatus.Available, Target: not null })
            return new(assignment.Status);

        var existing = await journal.ReadAsync(token);
        if (existing?.Id == request.UpdateId && (existing.Phase is UpdatePhase.Succeeded or UpdatePhase.RollbackFailed or UpdatePhase.Failed ||
            existing.Phase == UpdatePhase.RolledBack && !existing.RetryAllowed))
            return new(CurrentAssignmentStatus.Available, existing);
        if (existing is not null && existing.Phase is not (UpdatePhase.Succeeded or UpdatePhase.RolledBack or UpdatePhase.Failed))
            throw new InvalidOperationException("The previous update must be recovered first.");

        if (assignment.Target.RuntimeIdentifier != runtimeIdentifier)
            throw new InvalidDataException("The assigned package targets a different platform.");
        var previous = await runtime.GetSelectedDirectoryAsync(token);
        var currentVersion = ReadCurrentVersion(previous, existing);
        var target = assignment.Target;
        var provenance = new UpdateProvenance(Guid.ParseExact(request.Credential.AsSpan(4, 36), "D"), target.PackageId, target.ManifestSha256, target.PackageVersion);
        await using var package = await packages.AcquireAsync(target.ManifestSha256, target.PackageVersion, runtimeIdentifier,
            updaterVersion, currentVersion, request.Credential, token);
        using var stopProgress = CancellationTokenSource.CreateLinkedTokenSource(token);
        var progress = delivery is null ? Task.CompletedTask : ReportProgressAsync(request.Credential, stopProgress.Token);
        UpdateState result;
        try
        {
            result = await transaction.ApplyAsync(server, request.Credential, target, package.Manifest, package.Archive,
                installationRoot, previous, runtimeIdentifier, updaterVersion, currentVersion, token, provenance);
        }
        finally
        {
            await stopProgress.CancelAsync();
            await progress;
        }
        if (delivery is not null) await delivery.FlushAsync(server, request.Credential, token);
        return new(CurrentAssignmentStatus.Available, result);
    }

    internal static string? ReadCurrentVersion(string selectedDirectory, UpdateState? existing)
    {
        try { return InstalledAgentVersion.Read(selectedDirectory); }
        catch (Exception error) when (existing?.Phase == UpdatePhase.Failed &&
            error is IOException or UnauthorizedAccessException or InvalidDataException or BadImageFormatException)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var selected = Path.GetFullPath(selectedDirectory);
            if (string.Equals(Path.GetFullPath(existing.TargetDirectory), selected, comparison))
                return existing.Provenance?.PackageVersion;
            if (string.Equals(Path.GetFullPath(existing.PreviousDirectory), selected, comparison))
                return existing.PreviousAgentVersion;
            throw;
        }
    }

    private async Task ReportProgressAsync(string credential, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { await delivery!.ReportProgressAsync(server, credential, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { /* A progress observation never blocks the local update. */ }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

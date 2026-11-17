using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Packages;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Prepares an authenticated package and applies it only while its central assignment is current.</summary>
/// <remarks>The archive and manifest come from the authenticated package source. The caller keeps the
/// installation and journal directories private to the updater, and supplies an HTTP client with redirects disabled.</remarks>
public sealed class UpdateTransaction(HttpClient http, IUpdateJournal journal, IAgentUpdateRuntime runtime, TimeProvider clock)
{
    public async Task<UpdateState> ApplyAsync(Uri server, string credential, AgentVersionTarget target, VerifiedManifest verified,
        Stream archive, string installationRoot, string previousDirectory, string runtimeIdentifier,
        string updaterVersion, string? currentAgentVersion, CancellationToken token, UpdateProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(verified);
        var root = Path.GetFullPath(installationRoot);
        var previous = Path.GetFullPath(previousDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var executable = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? "Vigiloom.Agent.exe" : "Vigiloom.Agent";
        if (!previous.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison) ||
            !File.Exists(Path.Combine(previous, executable)))
            throw new InvalidOperationException("The previous Agent version is not available for recovery.");
        var manifest = PackageManifest.Parse(verified, clock.GetUtcNow());
        var verifier = new AssignmentVerifier(http, server, credential, target, manifest, clock);
        var authorization = await verifier.CheckAsync(target.UpdateId, token);
        if (authorization == UpdateAuthorization.Unavailable) throw new UpdateAuthorizationUnavailableException();
        if (authorization != UpdateAuthorization.Authorized) throw new InvalidOperationException("The update assignment is no longer authorized.");

        var existing = await journal.ReadAsync(token);
        if (existing is not null)
        {
            if (existing.Id == target.UpdateId)
            {
                if (!string.Equals(existing.PreviousDirectory, previousDirectory, comparison))
                    throw new InvalidOperationException("The update identifier already belongs to a different version selection.");
                if (existing.Phase is UpdatePhase.Succeeded or UpdatePhase.RollbackFailed ||
                    existing.Phase == UpdatePhase.RolledBack && !existing.RetryAllowed) return existing;
            }
            if (existing.Phase is not (UpdatePhase.Succeeded or UpdatePhase.RolledBack))
                throw new InvalidOperationException("The previous update must be recovered first.");
        }

        var selected = Path.GetFullPath(await runtime.GetSelectedDirectoryAsync(token));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(selected), Path.TrimEndingDirectorySeparator(previous), comparison))
            throw new InvalidOperationException("The previous Agent version does not match the service's selected version.");
        if (existing is not null && !string.Equals(Path.GetFullPath(existing.TargetDirectory), selected, comparison))
            PreparedPackageCleanup.RemoveRolledBack(existing, root);

        var prepared = await PackageStager.PrepareAsync(archive, manifest, target, installationRoot, runtimeIdentifier,
            updaterVersion, currentAgentVersion, clock, token);
        var coordinator = new UpdateCoordinator(journal, runtime, clock);
        // The coordinator records this directory before stopping the Agent. If the process is interrupted,
        // keep it for recovery; the installed version must never be deleted while it may be running.
        UpdateState result;
        try { result = await coordinator.ApplyAsync(target.UpdateId, previousDirectory, prepared, verifier.CheckAsync, token, provenance); }
        catch (Exception failure)
        {
            UpdateState? recorded;
            try { recorded = await journal.ReadAsync(CancellationToken.None); }
            catch (Exception journalFailure)
            { throw new IOException("Could not determine whether the prepared package is needed for recovery.", new AggregateException(failure, journalFailure)); }
            if (recorded is null || !string.Equals(recorded.TargetDirectory, prepared, comparison))
            {
                try { if (Directory.Exists(prepared)) Directory.Delete(prepared, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { throw new IOException("The unreferenced prepared package could not be removed.", new AggregateException(failure, cleanup)); }
            }
            throw;
        }
        PreparedPackageCleanup.RemoveRolledBack(result, root);
        return result;
    }
}

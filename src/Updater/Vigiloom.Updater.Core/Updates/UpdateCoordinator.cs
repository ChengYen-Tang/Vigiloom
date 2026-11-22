namespace Vigiloom.Updater.Core.Updates;

public enum UpdateAuthorization { Authorized, NoLongerAuthorized, Unavailable }

/// <summary>The current assignment could not be checked; the same update can be retried.</summary>
public sealed class UpdateAuthorizationUnavailableException : Exception;

/// <summary>Controls the installed Agent service and checks its local readiness.</summary>
public interface IAgentUpdateRuntime
{
    Task<string> GetSelectedDirectoryAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
    Task SelectVersionAsync(string versionDirectory, CancellationToken token);
    Task StartAsync(CancellationToken token);
    Task<bool> WaitHealthyAsync(CancellationToken token);
}

/// <summary>Switches a prepared version and preserves failed or interrupted updates for administrator action.</summary>
public sealed class UpdateCoordinator(IUpdateJournal journal, IAgentUpdateRuntime runtime, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <param name="isCurrent">Checks the current authenticated assignment, revocation and manifest authorization for this update.</param>
    public async Task<UpdateState> ApplyAsync(Guid id, string previousDirectory, string targetDirectory,
        Func<Guid, CancellationToken, Task<UpdateAuthorization>> isCurrent, CancellationToken token,
        UpdateProvenance? provenance = null, string? pendingUpdaterDirectory = null, string? previousAgentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        if (id.Version != 7 || string.IsNullOrWhiteSpace(previousDirectory) || string.IsNullOrWhiteSpace(targetDirectory) ||
            string.Equals(previousDirectory, targetDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("An update requires distinct previous and target directories.");
        var existing = await journal.ReadAsync(token);
        if (existing?.Id == id)
        {
            if (existing.Provenance is not null && existing.Provenance != provenance)
                throw new InvalidOperationException("The update identifier belongs to a different target.");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var retryable = existing.Phase == UpdatePhase.RolledBack && existing.RetryAllowed;
            if (!string.Equals(existing.PreviousDirectory, previousDirectory, comparison) ||
                !retryable && !string.Equals(existing.TargetDirectory, targetDirectory, comparison))
                throw new InvalidOperationException("The update identifier already belongs to a different version selection.");
            if (!retryable) return existing;
        }
        if (existing is not null && existing.Phase is not (UpdatePhase.Succeeded or UpdatePhase.RolledBack or UpdatePhase.Failed))
            throw new InvalidOperationException("The previous update must be recovered first.");
        // A temporary inability to verify must not interrupt the running Agent or consume this update ID.
        await EnsureAuthorizedAsync(isCurrent, id, token);
        var now = clock.GetUtcNow();
        var steps = existing?.Id == id && existing.RetryAllowed
            ? existing.Steps.Append(new(UpdatePhase.Prepared, now, null, Guid.CreateVersion7())).ToArray()
            : [new(UpdatePhase.Prepared, now, null, Guid.CreateVersion7())];
        var retained = existing?.Phase == UpdatePhase.Failed
            ? (existing.RetainedFailedDirectories ?? []).Append(existing.TargetDirectory).Distinct(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray()
            : existing?.RetainedFailedDirectories;
        var state = new UpdateState(id, previousDirectory, targetDirectory, UpdatePhase.Prepared, now, steps,
            Provenance: provenance, RetainedFailedDirectories: retained, PendingUpdaterDirectory: pendingUpdaterDirectory,
            PreviousAgentVersion: previousAgentVersion);
        await journal.WriteAsync(state, token);
        try
        {
            state = await AdvanceAsync(state, UpdatePhase.Stopping, token);
            await runtime.StopAsync(token);
            // A cancelled or replaced assignment must not be applied after a slow service stop.
            await EnsureAuthorizedAsync(isCurrent, id, token);
            state = await AdvanceAsync(state, UpdatePhase.Switching, token);
            await runtime.SelectVersionAsync(targetDirectory, token);
            state = await AdvanceAsync(state, UpdatePhase.Starting, token);
            await runtime.StartAsync(token);
            state = await AdvanceAsync(state, UpdatePhase.Verifying, token);
            if (!await runtime.WaitHealthyAsync(token)) throw new InvalidOperationException("The new Agent did not become healthy.");
            return await AdvanceAsync(state, UpdatePhase.Succeeded, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (state.Phase == UpdatePhase.Prepared) throw;
            return await AdvanceAsync(state with { Error = error.GetType().Name, RetryAllowed = false }, UpdatePhase.Failed, token);
        }
    }

    private static async Task EnsureAuthorizedAsync(Func<Guid, CancellationToken, Task<UpdateAuthorization>> check, Guid id, CancellationToken token)
    {
        UpdateAuthorization result;
        try { result = await check(id, token); }
        catch (Exception error) when (error is HttpRequestException or TimeoutException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        { throw new UpdateAuthorizationUnavailableException(); }
        if (result == UpdateAuthorization.Unavailable) throw new UpdateAuthorizationUnavailableException();
        if (result != UpdateAuthorization.Authorized) throw new InvalidOperationException("The update assignment is no longer authorized.");
    }


    /// <summary>Records an interrupted switch without changing the selected Agent version.</summary>
    public async Task<UpdateState?> RecoverAsync(CancellationToken token)
    {
        var state = await journal.ReadAsync(token);
        if (state is null || state.Phase is UpdatePhase.Succeeded or UpdatePhase.RolledBack or UpdatePhase.Failed) return state;
        return await AdvanceAsync(state with { Error = state.Error ?? "Interrupted", RetryAllowed = false }, UpdatePhase.Failed, token);
    }

    private async Task<UpdateState> AdvanceAsync(UpdateState state, UpdatePhase phase, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        state = state with { Phase = phase, UpdatedAt = now, Steps = [.. state.Steps, new(phase, now, state.Error, Guid.CreateVersion7())] };
        await journal.WriteAsync(state, token);
        return state;
    }
}

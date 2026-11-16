namespace Vigiloom.Updater.Core.Updates;

/// <summary>Removes only a completed rollback's updater-created staging directory.</summary>
public static class PreparedPackageCleanup
{
    public static void RemoveRolledBack(UpdateState state, string installationRoot)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != UpdatePhase.RolledBack) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var staging = Path.GetFullPath(Path.Combine(installationRoot, "staging"));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.TargetDirectory));
        var previous = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.PreviousDirectory));
        var name = Path.GetFileName(target);
        if (string.Equals(target, previous, comparison) || !string.Equals(Path.GetDirectoryName(target), staging, comparison) ||
            !Guid.TryParseExact(name, "N", out var id) || id.Version != 7) return;
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}

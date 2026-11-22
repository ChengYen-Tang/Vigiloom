namespace Vigiloom.Updater.Core.Updates;

/// <summary>Removes only a completed rollback's updater-created staging directory.</summary>
public static class PreparedPackageCleanup
{
    /// <summary>Remove abandoned preparation directories not referenced by the service or durable update state.</summary>
    public static void RemoveUnreferenced(UpdateState? state, string installationRoot, string selectedDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var keep = new HashSet<string>(comparison) { Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory)) };
        if (state is not null)
        {
            keep.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.PreviousDirectory)));
            keep.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.TargetDirectory)));
            foreach (var retained in state.RetainedFailedDirectories ?? [])
                keep.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(retained)));
        }
        var staging = new DirectoryInfo(Path.Combine(Path.GetFullPath(installationRoot), "staging"));
        if (!staging.Exists || (staging.Attributes & FileAttributes.ReparsePoint) != 0) return;
        foreach (var directory in staging.EnumerateDirectories())
        {
            if (!Guid.TryParseExact(directory.Name, "N", out var id) || id.Version != 7 ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                keep.Contains(Path.TrimEndingDirectorySeparator(directory.FullName))) continue;
            directory.Delete(recursive: true);
        }
    }

    /// <summary>After a successful administrator-directed replacement, remove failed staging directories.</summary>
    public static void RemoveRetainedFailures(UpdateState state, string installationRoot)
    {
        if (state.Phase != UpdatePhase.Succeeded || state.RetainedFailedDirectories is null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var staging = Path.GetFullPath(Path.Combine(installationRoot, "staging"));
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.TargetDirectory));
        foreach (var retained in state.RetainedFailedDirectories)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(retained));
            if (string.Equals(path, selected, comparison) || !string.Equals(Path.GetDirectoryName(path), staging, comparison) ||
                !Guid.TryParseExact(Path.GetFileName(path), "N", out var id) || id.Version != 7 || !Directory.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(path, recursive: true);
        }
    }

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

    /// <summary>After a successful switch, keep the selected and previous versions for recovery.</summary>
    public static void RemoveSuperseded(UpdateState state, string installationRoot, string selectedDirectory)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Phase != UpdatePhase.Succeeded) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.TargetDirectory));
        var previous = Path.TrimEndingDirectorySeparator(Path.GetFullPath(state.PreviousDirectory));
        var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory));
        if (!string.Equals(target, selected, comparison))
            throw new InvalidOperationException("The selected Agent version does not match the completed update.");
        var staging = new DirectoryInfo(Path.Combine(Path.GetFullPath(installationRoot), "staging"));
        if (!staging.Exists || (staging.Attributes & FileAttributes.ReparsePoint) != 0) return;
        foreach (var directory in staging.EnumerateDirectories())
        {
            if (!Guid.TryParseExact(directory.Name, "N", out var id) || id.Version != 7 ||
                (directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var path = Path.TrimEndingDirectorySeparator(directory.FullName);
            if (!string.Equals(path, target, comparison) && !string.Equals(path, previous, comparison))
                directory.Delete(recursive: true);
        }
    }
}

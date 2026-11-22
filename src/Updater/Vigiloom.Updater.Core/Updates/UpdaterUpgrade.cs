using NuGet.Versioning;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Selects a bundled Updater only for an Agent upgrade and a newer Updater version.</summary>
public static class UpdaterUpgrade
{
    public static string? Select(string preparedDirectory, string runtimeIdentifier, string updaterVersion,
        string? currentAgentVersion, string targetAgentVersion)
    {
        var directory = Path.Combine(preparedDirectory, "updater");
        if (!Directory.Exists(directory)) return null;
        var executable = Path.Combine(directory, runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
            ? "Vigiloom.Updater.exe" : "Vigiloom.Updater");
        if (!File.Exists(executable)) throw new InvalidDataException("The bundled Updater executable is missing.");
        var candidate = InstalledAgentVersion.ReadUpdater(directory);
        return ShouldUpgrade(currentAgentVersion, targetAgentVersion, updaterVersion, candidate) ? directory : null;
    }

    internal static bool ShouldUpgrade(string? currentAgentVersion, string targetAgentVersion, string updaterVersion, string candidateVersion)
    {
        return NuGetVersion.TryParse(currentAgentVersion, out var currentAgent) &&
            NuGetVersion.TryParse(targetAgentVersion, out var targetAgent) &&
            NuGetVersion.TryParse(updaterVersion, out var currentUpdater) &&
            NuGetVersion.TryParse(candidateVersion, out var candidateUpdater) &&
            targetAgent > currentAgent && candidateUpdater > currentUpdater;
    }
}

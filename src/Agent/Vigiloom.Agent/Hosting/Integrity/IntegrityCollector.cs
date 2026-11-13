using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Hosting.Integrity;

/// <summary>Routes each scope to its native collector without associating services with hosts.</summary>
internal static class IntegrityCollector
{
    public static async Task<IntegrityReadResult> ReadAsync(IntegrityScope scope, CancellationToken token)
    {
        if (scope.Kind == IntegrityScopeKind.Directory)
        {
            if (OperatingSystem.IsWindows()) return await Platforms.Windows.Integrity.FileScopeReader.ReadAsync(scope, token);
            if (OperatingSystem.IsLinux()) return await Platforms.Linux.Integrity.FileScopeReader.ReadAsync(scope, token);
        }
        if (scope.Kind == IntegrityScopeKind.Registry && OperatingSystem.IsWindows())
            return Platforms.Windows.Integrity.RegistryScopeReader.Read(scope, token);
        return new(IntegrityScanOutcome.Unsupported, [], false);
    }
}

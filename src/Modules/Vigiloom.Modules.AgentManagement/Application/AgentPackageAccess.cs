using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Serves publisher data only for the authenticated Agent's current assignment.</summary>
public sealed class AgentPackageAccess(AgentVersionAssignments assignments, AgentPackageSourceSettings sources,
    UpdateTrustStore trust, IPackageLibraryTransport transport, IDbContextFactory<AgentManagementDbContext> contexts, TimeProvider clock)
{
    public async Task<byte[]?> ReadManifestAsync(string? credential, string name, CancellationToken token)
    {
        var target = (await assignments.GetForAgentAsync(credential, token)).Target;
        if (target is null || !IsManifestName(name, target.ManifestSha256)) return null;
        var verified = await VerifyAsync(target, token);
        if (verified is null || !await IsCurrentAsync(credential, target, token)) return null;
        return verified.Manifest.Content.ToArray();
    }

    public async Task<Stream?> OpenArchiveAsync(string? credential, string name, CancellationToken token)
    {
        var target = (await assignments.GetForAgentAsync(credential, token)).Target;
        if (target is null) return null;
        var verified = await VerifyAsync(target, token);
        if (verified is null || !string.Equals(name, verified.Package.Sha256, StringComparison.Ordinal)) return null;
        var stream = await transport.OpenAsync(verified.Source.BaseUri, verified.Source.Credential, verified.Package, token);
        try
        {
            if (await IsCurrentAsync(credential, target, token) && await SourceMatchesAsync(verified.Source, token)) return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
        await stream.DisposeAsync();
        return null;
    }

    private async Task<Verified?> VerifyAsync(AgentVersionTarget target, CancellationToken token)
    {
        var source = await sources.ReadEffectiveAsync(token);
        if (source is null) return null;
        VerifiedManifest manifest;
        await using (var lease = await trust.AcquireAsync(token))
            manifest = await transport.ReadManifestAsync(source.BaseUri, source.Credential, lease, target.ManifestSha256, token);
        var package = PackageManifest.Parse(manifest, clock.GetUtcNow());
        if (package.ManifestSha256 != target.ManifestSha256 || package.Version != target.PackageVersion ||
            package.RuntimeIdentifier != target.RuntimeIdentifier || !await SourceMatchesAsync(source, token)) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = clock.GetUtcNow();
        var matches = await db.Packages.AsNoTracking().AnyAsync(x => x.Id == target.PackageId && x.Listed && !x.Revoked &&
            x.VerifiedUntil > now && x.Version == package.Version && x.RuntimeIdentifier == package.RuntimeIdentifier &&
            x.ManifestSha256 == package.ManifestSha256 && x.Sha256 == package.Sha256 && x.Size == package.Size, token);
        return matches ? new(manifest, package, source) : null;
    }

    private async Task<bool> IsCurrentAsync(string? credential, AgentVersionTarget expected, CancellationToken token) =>
        (await assignments.GetForAgentAsync(credential, token)).Target == expected;

    private async Task<bool> SourceMatchesAsync(AgentPackageSourceSettings.EffectiveSource expected, CancellationToken token)
    {
        var current = await sources.ReadEffectiveAsync(token);
        return current is not null && current.Version.AsSpan().SequenceEqual(expected.Version);
    }

    private static bool IsManifestName(string name, string hash) =>
        string.Equals(name, hash + ".json", StringComparison.Ordinal) ||
        string.Equals(name, hash + "." + hash + ".json", StringComparison.Ordinal);

    private sealed record Verified(VerifiedManifest Manifest, PackageManifest Package, AgentPackageSourceSettings.EffectiveSource Source);
}

using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.UpdateSecurity;
using Vigiloom.UpdateSecurity.AgentPackages;

namespace Vigiloom.Modules.Resources.Application.AgentPackages;

/// <summary>Serves the package currently assigned to an eligible Agent.</summary>
public sealed class AgentPackageAccess(AgentAssignments assignments, IPackageLibraryTransport files,
    IDbContextFactory<ResourcesDbContext> contexts)
{
    public async Task<byte[]?> ReadManifestAsync(Guid agentId, string name, CancellationToken token)
    {
        var target = await assignments.GetTargetAsync(agentId, token);
        if (target is null || name != target.ManifestSha256 + ".json") return null;
        var verified = await VerifyAsync(target, token);
        return verified is not null && await assignments.GetTargetAsync(agentId, token) == target ? verified.Value.Content : null;
    }

    public async Task<Stream?> OpenArchiveAsync(Guid agentId, string name, CancellationToken token)
    {
        var target = await assignments.GetTargetAsync(agentId, token);
        if (target is null) return null;
        var verified = await VerifyAsync(target, token);
        if (verified is null || name != verified.Value.Package.Sha256) return null;
        var stream = await files.OpenCachedAsync(verified.Value.Package.Sha256, verified.Value.Package.Size, token);
        if (stream is null) return null;
        try { if (await assignments.GetTargetAsync(agentId, token) == target) return stream; }
        catch { await stream.DisposeAsync(); throw; }
        await stream.DisposeAsync();
        return null;
    }

    private async Task<(byte[] Content, PackageManifest Package)?> VerifyAsync(AgentVersionTarget target, CancellationToken token)
    {
        var content = await files.ReadManifestAsync(target.ManifestSha256, token);
        if (content is null) return null;
        var package = PackageManifest.Parse(new VerifiedManifest(content));
        if (package.ManifestSha256 != target.ManifestSha256 || package.Version != target.PackageVersion ||
            package.RuntimeIdentifier != target.RuntimeIdentifier) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var matches = await db.AgentPackages.AsNoTracking().AnyAsync(p => p.Id == target.PackageId && p.Version == package.Version &&
            p.RuntimeIdentifier == package.RuntimeIdentifier && p.ManifestSha256 == package.ManifestSha256 &&
            p.Sha256 == package.Sha256 && p.Size == package.Size, token);
        return matches ? (content, package) : null;
    }
}

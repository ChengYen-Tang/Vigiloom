using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Serves only the package currently assigned to an authenticated Agent.</summary>
public sealed class AgentPackageAccess(AgentVersionAssignments assignments, IPackageLibraryTransport transport,
    IDbContextFactory<AgentManagementDbContext> contexts)
{
    public async Task<byte[]?> ReadManifestAsync(string? credential, string name, CancellationToken token)
    {
        var target = (await assignments.GetForAgentAsync(credential, token)).Target;
        if (target is null || name != target.ManifestSha256 + ".json") return null;
        var verified = await VerifyAsync(target, token);
        return verified is not null && await IsCurrentAsync(credential, target, token) ? verified.Value.Content : null;
    }

    public async Task<Stream?> OpenArchiveAsync(string? credential, string name, CancellationToken token)
    {
        var target = (await assignments.GetForAgentAsync(credential, token)).Target;
        if (target is null) return null;
        var verified = await VerifyAsync(target, token);
        if (verified is null || name != verified.Value.Package.Sha256) return null;
        var stream = await transport.OpenCachedAsync(verified.Value.Package.Sha256, verified.Value.Package.Size, token);
        if (stream is null) return null;
        try { if (await IsCurrentAsync(credential, target, token)) return stream; }
        catch { await stream.DisposeAsync(); throw; }
        await stream.DisposeAsync();
        return null;
    }

    private async Task<(byte[] Content, PackageManifest Package)?> VerifyAsync(AgentVersionTarget target, CancellationToken token)
    {
        var content = await transport.ReadManifestAsync(target.ManifestSha256, token);
        if (content is null) return null;
        var package = PackageManifest.Parse(new VerifiedManifest(content));
        if (package.ManifestSha256 != target.ManifestSha256 || package.Version != target.PackageVersion ||
            package.RuntimeIdentifier != target.RuntimeIdentifier) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var matches = await db.Packages.AsNoTracking().AnyAsync(x => x.Id == target.PackageId &&
            x.Version == package.Version && x.RuntimeIdentifier == package.RuntimeIdentifier &&
            x.ManifestSha256 == package.ManifestSha256 && x.Sha256 == package.Sha256 && x.Size == package.Size, token);
        return matches ? (content, package) : null;
    }

    private async Task<bool> IsCurrentAsync(string? credential, AgentVersionTarget expected, CancellationToken token) =>
        (await assignments.GetForAgentAsync(credential, token)).Target == expected;
}

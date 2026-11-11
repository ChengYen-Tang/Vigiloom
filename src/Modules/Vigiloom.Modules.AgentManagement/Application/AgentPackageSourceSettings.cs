using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Controls the authenticated publisher origin and invalidates stale package availability on change.</summary>
public sealed class AgentPackageSourceSettings(IDbContextFactory<AgentManagementDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.AgentPackageSource.Credential.v1");

    public async Task<AgentPackageSourceView?> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.PackageSources.AsNoTracking().SingleOrDefaultAsync(token);
        return source is null ? null : View(source);
    }

    public static bool Valid(AgentPackageSourceUpdate? value)
    {
        if (value is null || value.BaseUri is not { Length: > 0 and <= 2048 } ||
            !Uri.TryCreate(value.BaseUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/') ||
            uri.AbsoluteUri.Length > 2048 || value.Version is null || value.Version.Length > 32)
            return false;
        return value.CredentialAction switch
        {
            PackageCredentialAction.Keep or PackageCredentialAction.Clear => value.Credential is null,
            PackageCredentialAction.Replace => value.Credential is { Length: > 0 and <= 4096 } &&
                value.Credential.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='),
            _ => false
        };
    }

    /// <returns>The saved view, or null if another administrator changed the source.</returns>
    public async Task<AgentPackageSourceView?> UpdateAsync(AgentPackageSourceUpdate value, Guid actor, CancellationToken token)
    {
        if (!Valid(value) || actor == Guid.Empty) throw new ArgumentException("Invalid publisher source or administrator.");
        byte[] expected;
        try { expected = value.Version.Length == 0 ? [] : Convert.FromBase64String(value.Version); }
        catch (FormatException) { return null; }
        if (expected.Length is not (0 or 8)) return null;
        var uri = new Uri(value.BaseUri, UriKind.Absolute).AbsoluteUri;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var current = await db.PackageSources.FromSqlRaw("SELECT * FROM [agent].[PackageSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Scope] = 1").SingleOrDefaultAsync(token);
        if (current is null && expected.Length != 0 || current is not null && !expected.AsSpan().SequenceEqual(current.Version)) return null;
        var oldVersion = current is null ? "" : Convert.ToBase64String(current.Version);
        var addressChanged = current is null || !string.Equals(current.BaseUri, uri, StringComparison.Ordinal);
        if (addressChanged && current?.ProtectedCredential is not null && value.CredentialAction == PackageCredentialAction.Keep)
            throw new ArgumentException("Changing the publisher address requires clearing or replacing its credential.");
        var credentialChanged = value.CredentialAction == PackageCredentialAction.Replace ||
            value.CredentialAction == PackageCredentialAction.Clear && current?.ProtectedCredential is not null;
        if (!addressChanged && !credentialChanged) return View(current!);
        current ??= new AgentPackageSource { Id = Guid.CreateVersion7(), BaseUri = uri };
        if (db.Entry(current).State == EntityState.Detached) db.PackageSources.Add(current);
        current.BaseUri = uri;
        if (value.CredentialAction == PackageCredentialAction.Replace) current.ProtectedCredential = protector.Protect(value.Credential!);
        else if (value.CredentialAction == PackageCredentialAction.Clear) current.ProtectedCredential = null;
        await db.SaveChangesAsync(token);
        await db.Packages.Where(x => x.Listed).ExecuteUpdateAsync(x => x.SetProperty(p => p.Listed, false), token);
        var now = clock.GetUtcNow().ToUniversalTime();
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, current.Id, now, new AgentPackageSourceChanged(oldVersion, Convert.ToBase64String(current.Version), addressChanged, credentialChanged)), token);
        await transaction.CommitAsync(token);
        return View(current);
    }

    internal async Task<EffectiveSource?> ReadEffectiveAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.PackageSources.AsNoTracking().SingleOrDefaultAsync(token);
        return source is null ? null : new EffectiveSource(new Uri(source.BaseUri),
            source.ProtectedCredential is null ? null : protector.Unprotect(source.ProtectedCredential), source.Version);
    }

    /// <summary>Refreshes only from the configured publisher and rejects a concurrent source change.</summary>
    public async Task<bool> RefreshAsync(UpdateTrustStore trust, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (actor == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actor));
        return await RefreshCoreAsync(trust, actor, token);
    }

    public Task<bool> RefreshScheduledAsync(UpdateTrustStore trust, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(trust);
        return RefreshCoreAsync(trust, null, token);
    }

    private async Task<bool> RefreshCoreAsync(UpdateTrustStore trust, Guid? actor, CancellationToken token)
    {
        var source = await ReadEffectiveAsync(token);
        if (source is null) return false;
        var metadata = new Uri(source.BaseUri, "metadata/");
        var targets = new Uri(source.BaseUri, "targets/");
        var reader = source.Credential is null ? new TrustedCatalogueReader(metadata, targets) :
            new TrustedCatalogueReader(metadata, targets, source.Credential);
        await trust.RefreshCatalogueAsync(reader, clock, source.Version, actor, token);
        return true;
    }

    private static AgentPackageSourceView View(AgentPackageSource source) =>
        new(source.BaseUri, source.ProtectedCredential is not null, Convert.ToBase64String(source.Version));

    internal sealed class EffectiveSource(Uri baseUri, string? credential, byte[] version)
    {
        public Uri BaseUri { get; } = baseUri;
        public string? Credential { get; } = credential;
        public byte[] Version { get; } = version;
    }
}

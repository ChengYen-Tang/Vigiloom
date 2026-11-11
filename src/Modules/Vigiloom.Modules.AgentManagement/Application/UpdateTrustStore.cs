using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Persists update trust state while holding a SQL session lock across each refresh.</summary>
public sealed class UpdateTrustStore(IDbContextFactory<AgentManagementDbContext> contexts, IOperationAuditWriter<OperationAudit> audit)
{
    private const string LockName = "vigiloom.agent.update.trust";

    public async Task<AgentPackageTrustView> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var state = await context.UpdateTrustStates.AsNoTracking().Select(x => new { x.Snapshot, x.Digest }).SingleOrDefaultAsync(cancellationToken);
        if (state is null) return new(false, null, null);
        var root = TrustMetadataSnapshot.Decode(state.Snapshot, state.Digest).Read("root")!;
        return new(true, MetadataVersion(root), Convert.ToHexStringLower(SHA256.HashData(root)));
    }

    /// <summary>Explicitly initializes a new store from an independently provisioned trusted root.</summary>
    public Task ProvisionAsync(byte[] trustedRoot, CancellationToken cancellationToken = default) =>
        ProvisionCoreAsync(trustedRoot, null, cancellationToken);

    /// <summary>Initializes trust only after an administrator confirms an independently obtained fingerprint.</summary>
    public async Task ProvisionForAdministratorAsync(byte[] trustedRoot, string expectedSha256, Guid actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedRoot);
        if (actor == Guid.Empty || expectedSha256 is not { Length: 64 } ||
            !expectedSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("An administrator and lowercase SHA-256 fingerprint are required.");
        var actual = SHA256.HashData(trustedRoot);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("The trusted root fingerprint does not match.");
        await BootstrapRoot.ValidateAsync(trustedRoot, cancellationToken);
        await ProvisionCoreAsync(trustedRoot, actor, cancellationToken);
    }

    private async Task ProvisionCoreAsync(byte[] trustedRoot, Guid? actor, CancellationToken cancellationToken)
    {
        var snapshot = TrustMetadataSnapshot.Provision(trustedRoot);
        var context = await LockAsync(cancellationToken);
        try
        {
            if (await context.UpdateTrustStates.AnyAsync(cancellationToken))
                throw new InvalidOperationException("Update trust is already provisioned.");
            var (data, digest) = snapshot.Encode();
            var id = Guid.CreateVersion7();
            var rootVersion = MetadataVersion(trustedRoot);
            var rootDigest = SHA256.HashData(trustedRoot);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                IF COALESCE(APPLOCK_MODE('public', {LockName}, 'Session'), 'NoLock') <> 'Exclusive'
                    THROW 51000, 'Update trust lock was lost.', 1;
                INSERT INTO [agent].[UpdateTrustStates] ([Id], [Scope], [Snapshot], [Digest])
                VALUES ({id}, 1, {data}, {digest});
                INSERT INTO [agent].[UpdateRoots] ([Version], [Metadata], [Digest])
                VALUES ({rootVersion}, {trustedRoot}, {rootDigest});
                """, cancellationToken);
            if (actor is { } administrator)
                await audit.AppendAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(),
                    new OperationAudit(administrator, id, DateTimeOffset.UtcNow,
                        new AgentPackageTrustInitialized(rootVersion, Convert.ToHexStringLower(rootDigest))), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { await ReleaseAsync(context); }
    }

    private static int MetadataVersion(byte[] metadata)
    {
        using var json = JsonDocument.Parse(metadata);
        return json.RootElement.GetProperty("signed").GetProperty("version").GetInt32();
    }

    public async Task<ITrustMetadataLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var context = await LockAsync(cancellationToken);
        try
        {
            var state = await context.UpdateTrustStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("Update trust has not been provisioned.");
            var snapshot = TrustMetadataSnapshot.Decode(state.Snapshot, state.Digest);
            return new Lease(context, state.Id, state.Digest, snapshot);
        }
        catch
        {
            await ReleaseAsync(context);
            throw;
        }
    }

    /// <summary>Refreshes the authenticated package list without publishing a partial catalogue.</summary>
    public async Task RefreshCatalogueAsync(TrustedCatalogueReader reader, TimeProvider clock, byte[] expectedSourceVersion, Guid? actor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clock);
        if (expectedSourceVersion is not { Length: 8 }) throw new ArgumentException("The source revision is required.", nameof(expectedSourceVersion));
        if (actor == Guid.Empty) throw new ArgumentException("The administrator identifier is invalid.", nameof(actor));
        await using var lease = (Lease)await AcquireAsync(cancellationToken);
        var verified = await reader.ReadAsync(lease, cancellationToken);
        var catalogue = PackageCatalogue.Parse(verified, clock.GetUtcNow());
        await lease.PublishAsync(catalogue, clock, expectedSourceVersion, audit, actor, cancellationToken);
    }

    /// <summary>Returns original publisher metadata; target files require separate Agent authorization.</summary>
    public async Task<byte[]?> ReadMetadataAsync(string role, int? version, CancellationToken cancellationToken = default)
    {
        if (role is not ("root" or "timestamp" or "snapshot" or "targets") || version is <= 0 || role == "root" && version is null)
            throw new ArgumentException("A supported metadata role and version are required.");
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        if (role == "root")
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var currentState = await context.UpdateTrustStates.AsNoTracking().Select(x => new { x.Snapshot, x.Digest }).SingleOrDefaultAsync(cancellationToken);
            if (currentState is null) return null;
            var currentRoot = TrustMetadataSnapshot.Decode(currentState.Snapshot, currentState.Digest).Read("root")!;
            var currentVersion = MetadataVersion(currentRoot);
            var root = await context.UpdateRoots.AsNoTracking().Where(x => x.Version == version)
                .Select(x => new { x.Metadata, x.Digest }).SingleOrDefaultAsync(cancellationToken);
            if (root is null)
            {
                if (version <= currentVersion) throw new InvalidDataException("Stored root history is incomplete.");
                return null;
            }
            if (version > currentVersion) throw new InvalidDataException("Stored root history is ahead of current trust.");
            if (root.Metadata.Length is 0 or > 1048576 || root.Digest.Length != 32 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(root.Metadata), root.Digest) || MetadataVersion(root.Metadata) != version)
                throw new InvalidDataException("Stored root history is corrupt.");
            return root.Metadata;
        }
        var state = await context.UpdateTrustStates.AsNoTracking().Select(x => new { x.Snapshot, x.Digest }).SingleOrDefaultAsync(cancellationToken);
        if (state is null) return null;
        var data = TrustMetadataSnapshot.Decode(state.Snapshot, state.Digest).Read(role);
        return data is null || version is not null && MetadataVersion(data) != version ? null : data;
    }

    private async Task<AgentManagementDbContext> LockAsync(CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken);
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 10000; SELECT @result;";
            var name = command.CreateParameter();
            name.ParameterName = "@name";
            name.DbType = DbType.String;
            name.Value = LockName;
            command.Parameters.Add(name);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) < 0)
                throw new TimeoutException("Could not acquire the update trust lock.");
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask ReleaseAsync(AgentManagementDbContext context)
    {
        try
        {
            if (context.Database.GetDbConnection().State == ConnectionState.Open)
                await context.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_releaseapplock @Resource = {LockName}, @LockOwner = 'Session';");
        }
        finally { await context.DisposeAsync(); }
    }

    private sealed class Lease(AgentManagementDbContext context, Guid id, byte[] digest, TrustMetadataSnapshot snapshot) : ITrustMetadataLease
    {
        private bool disposed;

        public byte[]? LoadMetadata(string role)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return snapshot.Read(role);
        }

        public void StoreMetadata(string role, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = snapshot.Read(role);
            if (current is not null && data.SequenceEqual(current)) return;
            var next = snapshot.WithMetadata(role, data);
            var (encoded, nextDigest) = next.Encode();
            // TUF's callback is synchronous. Commit each accepted role before it returns.
            using var transaction = context.Database.BeginTransaction();
            context.Database.ExecuteSqlInterpolated($"""
                IF COALESCE(APPLOCK_MODE('public', {LockName}, 'Session'), 'NoLock') <> 'Exclusive'
                    THROW 51000, 'Update trust lock was lost.', 1;
                UPDATE [agent].[UpdateTrustStates] SET [Snapshot] = {encoded}, [Digest] = {nextDigest}
                WHERE [Id] = {id} AND [Digest] = {digest};
                IF @@ROWCOUNT <> 1 THROW 51000, 'Update trust state changed or is missing.', 1;
                UPDATE [agent].[Packages] SET [Listed] = 0 WHERE [Listed] = 1;
                """);
            if (role == "root")
            {
                var root = data.ToArray();
                context.UpdateRoots.Add(new UpdateRoot { Version = MetadataVersion(root), Metadata = root, Digest = SHA256.HashData(root) });
                context.SaveChanges();
            }
            transaction.Commit();
            digest = nextDigest;
            snapshot = next;
        }

        public async Task VerifyHeldAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (context.Database.GetDbConnection().State != ConnectionState.Open)
                throw new InvalidOperationException("Update trust connection was lost.");
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT APPLOCK_MODE('public', @name, 'Session')";
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.DbType = DbType.String;
            parameter.Value = LockName;
            command.Parameters.Add(parameter);
            var mode = await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(mode as string, "Exclusive", StringComparison.Ordinal))
                throw new InvalidOperationException("Update trust lock was lost.");
        }

        public async Task PublishAsync(PackageCatalogue catalogue, TimeProvider clock, byte[] expectedSourceVersion,
            IOperationAuditWriter<OperationAudit> audit, Guid? actor, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await VerifyHeldAsync(cancellationToken);
            var source = await context.PackageSources.FromSqlRaw("SELECT * FROM [agent].[PackageSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Scope] = 1")
                .SingleOrDefaultAsync(cancellationToken);
            if (source is null || !expectedSourceVersion.AsSpan().SequenceEqual(source.Version))
                throw new InvalidOperationException("The publisher source changed during catalogue refresh.");
            var versions = catalogue.Packages.Select(x => x.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var existing = await context.Packages.Where(x => x.Listed || versions.Contains(x.Version)).ToListAsync(cancellationToken);
            var byIdentity = existing.ToDictionary(x => (x.Version.ToUpperInvariant(), x.RuntimeIdentifier), x => x);
            foreach (var package in existing) package.Listed = false;
            foreach (var entry in catalogue.Packages)
            {
                var key = (entry.Version.ToUpperInvariant(), entry.Rid);
                if (!byIdentity.TryGetValue(key, out var package))
                {
                    package = new AgentPackage
                    {
                        Id = Guid.CreateVersion7(), Version = entry.Version, RuntimeIdentifier = entry.Rid,
                        Sha256 = entry.Sha256, ManifestSha256 = entry.ManifestSha256, Size = entry.Size
                    };
                    context.Packages.Add(package);
                }
                else if (package.Sha256 != entry.Sha256 || package.Size != entry.Size ||
                    package.ManifestSha256.Length != 0 && package.ManifestSha256 != entry.ManifestSha256)
                    throw new InvalidDataException("A published package version cannot change its content.");
                if (package.ManifestSha256.Length == 0) package.ManifestSha256 = entry.ManifestSha256;
                package.Revoked = entry.Revoked;
                package.Listed = true;
                package.VerifiedUntil = catalogue.Expires;
            }
            if (catalogue.Expires <= clock.GetUtcNow()) throw new InvalidDataException("Catalogue expired before publication.");
            await context.SaveChangesAsync(cancellationToken);
            if (actor is { } administrator)
                await audit.AppendAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(),
                    new OperationAudit(administrator, source.Id, clock.GetUtcNow().ToUniversalTime(),
                        new AgentPackageCatalogueRefreshed(catalogue.Packages.Count, Convert.ToBase64String(source.Version))), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await ReleaseAsync(context);
        }
    }
}

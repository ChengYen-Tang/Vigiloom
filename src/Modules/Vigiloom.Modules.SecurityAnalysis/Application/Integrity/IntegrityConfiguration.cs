using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

public enum IntegrityConfigurationStatus { Success, NotFound, Conflict, LimitReached }
public sealed record IntegrityConfigurationResult(IntegrityConfigurationStatus Status, IntegrityScopeView? Scope = null);

/// <summary>Per-device scopes with optimistic concurrency and transactional audit.</summary>
public sealed class IntegrityConfiguration(IDbContextFactory<SecurityAnalysisDbContext> contexts, IDeviceAgentSource devices, TimeProvider clock)
{
    public async Task<IReadOnlyList<IntegrityScopeView>?> ListAsync(Guid deviceId, CancellationToken token)
    {
        if (deviceId == Guid.Empty || await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.IntegrityScopes.AsNoTracking().Where(x => x.DeviceId == deviceId && x.RetiredAt == null)
            .OrderBy(x => x.RowId).ToListAsync(token);
        return rows.Select(View).ToArray();
    }

    public async Task<IntegrityConfigurationResult> CreateAsync(Guid deviceId, IntegrityScopeInput input, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (deviceId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Device and actor are required.");
        var row = new IntegrityScopeSetting { DeviceId = deviceId, Revision = 1, UpdatedBy = actor, UpdatedAt = clock.GetUtcNow() };
        var scope = input.WithIdentity(row.Id, row.Revision);
        if (!scope.IsValid()) throw new ArgumentException("Invalid integrity scope.", nameof(input));
        row.DefinitionJson = SerializeScope(scope);
        if (await devices.GetAsync(deviceId, token) is null) return new(IntegrityConfigurationStatus.NotFound);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var policy = await LockPolicyAsync(db, deviceId, token);
        var active = await db.IntegrityScopes.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityScopes] WITH (UPDLOCK, HOLDLOCK) WHERE [DeviceId] = {deviceId} AND [RetiredAt] IS NULL")
            .ToListAsync(token);
        if (active.Count >= IntegrityScope.MaximumScopes) return new(IntegrityConfigurationStatus.LimitReached);
        if (policy is null) db.IntegrityPolicyStates.Add(new IntegrityPolicyState { DeviceId = deviceId, Generation = 1 });
        else policy.Generation = checked(policy.Generation + 1);
        db.IntegrityScopes.Add(row);
        db.IntegrityScopeAudits.Add(Audit(row, scope.Enabled, IntegrityScopeAction.Created));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(IntegrityConfigurationStatus.Success, View(row));
    }

    public async Task<IntegrityConfigurationResult> UpdateAsync(Guid deviceId, Guid scopeId, IntegrityScopeInput input, string? versionText, Guid actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (deviceId == Guid.Empty || scopeId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Device, scope and actor are required.");
        if (!TryVersion(versionText, out var version)) return new(IntegrityConfigurationStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var policy = await LockPolicyAsync(db, deviceId, token);
        var row = await db.IntegrityScopes.SingleOrDefaultAsync(x => x.Id == scopeId && x.DeviceId == deviceId && x.RetiredAt == null, token);
        if (row is null) return new(IntegrityConfigurationStatus.NotFound);
        if (!version.AsSpan().SequenceEqual(row.Version)) return new(IntegrityConfigurationStatus.Conflict);
        var current = View(row);
        if (current.Scope.SamePolicy(input.WithIdentity(row.Id, row.Revision))) return new(IntegrityConfigurationStatus.Success, current);
        var next = input.WithIdentity(row.Id, checked(row.Revision + 1));
        if (!next.IsValid()) throw new ArgumentException("Invalid integrity scope.", nameof(input));
        if (policy is null) throw new InvalidDataException("Integrity policy generation is missing.");
        policy.Generation = checked(policy.Generation + 1);
        row.Revision = next.Revision;
        row.DefinitionJson = SerializeScope(next);
        row.UpdatedBy = actor;
        row.UpdatedAt = clock.GetUtcNow();
        db.IntegrityScopeAudits.Add(Audit(row, next.Enabled, IntegrityScopeAction.Updated));
        try
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(IntegrityConfigurationStatus.Success, View(row));
        }
        catch (DbUpdateConcurrencyException) { return new(IntegrityConfigurationStatus.Conflict); }
    }

    public async Task<IntegrityConfigurationResult> RetireAsync(Guid deviceId, Guid scopeId, string? versionText, Guid actor, CancellationToken token)
    {
        if (deviceId == Guid.Empty || scopeId == Guid.Empty || actor == Guid.Empty) throw new ArgumentException("Device, scope and actor are required.");
        if (!TryVersion(versionText, out var version)) return new(IntegrityConfigurationStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var policy = await LockPolicyAsync(db, deviceId, token);
        var row = await db.IntegrityScopes.SingleOrDefaultAsync(x => x.Id == scopeId && x.DeviceId == deviceId && x.RetiredAt == null, token);
        if (row is null) return new(IntegrityConfigurationStatus.NotFound);
        if (!version.AsSpan().SequenceEqual(row.Version)) return new(IntegrityConfigurationStatus.Conflict);
        if (policy is null) throw new InvalidDataException("Integrity policy generation is missing.");
        policy.Generation = checked(policy.Generation + 1);
        row.UpdatedBy = actor;
        row.UpdatedAt = clock.GetUtcNow();
        row.RetiredAt = row.UpdatedAt;
        db.IntegrityScopeAudits.Add(Audit(row, false, IntegrityScopeAction.Retired));
        try
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(IntegrityConfigurationStatus.Success, View(row));
        }
        catch (DbUpdateConcurrencyException) { return new(IntegrityConfigurationStatus.Conflict); }
    }

    internal static IntegrityScopeView View(IntegrityScopeSetting row)
    {
        var scope = JsonSerializer.Deserialize<IntegrityScope>(row.DefinitionJson);
        if (scope is null || !scope.IsValid() || scope.Id != row.Id || scope.Revision != row.Revision)
            throw new InvalidDataException("Stored integrity scope is invalid.");
        return new(scope, Convert.ToBase64String(row.Version));
    }

    private static IntegrityScopeAudit Audit(IntegrityScopeSetting row, bool enabled, IntegrityScopeAction action) => new()
    {
        ScopeId = row.Id, DeviceId = row.DeviceId, ActorId = row.UpdatedBy, Revision = row.Revision,
        Action = action, Enabled = enabled, OccurredAt = row.UpdatedAt
    };

    private static string SerializeScope(IntegrityScope scope)
    {
        var json = JsonSerializer.Serialize(scope);
        if (json.Length > 32768) throw new ArgumentException("Integrity scope is too large.");
        return json;
    }

    private static Task<IntegrityPolicyState?> LockPolicyAsync(SecurityAnalysisDbContext db, Guid deviceId, CancellationToken token) =>
        db.IntegrityPolicyStates.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityPolicyStates] WITH (XLOCK, HOLDLOCK) WHERE [DeviceId] = {deviceId}")
            .SingleOrDefaultAsync(token);

    private static bool TryVersion(string? text, out byte[] version)
    {
        try { version = Convert.FromBase64String(text ?? ""); return version.Length == 8; }
        catch (FormatException) { version = []; return false; }
    }
}

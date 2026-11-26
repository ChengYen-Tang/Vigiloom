using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using static Vigiloom.Infrastructure.Authentication.AgentCredentials;

using Vigiloom.Modules.Resources.Application;

namespace Vigiloom.Modules.Resources.Application.AgentIdentity;

/// <summary>Owns device-bound Agent registration and credentials.</summary>
public sealed class AgentRegistration(IDbContextFactory<ResourcesDbContext> contexts, TimeProvider clock, ResourceWork outbox)
{
    public async Task<IReadOnlyList<DeviceAgentOverview>> OverviewAsync(IReadOnlyList<Guid> deviceIds, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var devices = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id))
            .Select(d => new DeviceAgentState(d.Id, d.AgentAuthorizationGeneration, d.DeepMonitoring && d.IsEnabled && !d.IsArchived)).ToArrayAsync(token);
        var agents = await db.Agents.AsNoTracking().Where(a => deviceIds.Contains(a.DeviceId) && a.RevokedAt == null)
            .Select(a => new { a.DeviceId, Summary = new AgentRegistrationSummary(a.Id, a.DeviceGeneration, a.CreatedAt, a.RevokedAt) }).ToArrayAsync(token);
        var byDevice = agents.ToLookup(a => a.DeviceId);
        return devices.Select(device => new DeviceAgentOverview(device,
            byDevice[device.DeviceId].Select(x => x.Summary).SingleOrDefault(x => x.DeviceGeneration == device.Generation))).ToArray();
    }

    public async Task<DeviceAgentRegistrations?> ListAsync(Guid deviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var generation = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => (long?)d.AgentAuthorizationGeneration).SingleOrDefaultAsync(token);
        if (generation is null) return null;
        var agents = await db.Agents.AsNoTracking().Where(a => a.DeviceId == deviceId).OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.RowId)
            .Take(50).Select(a => new AgentRegistrationSummary(a.Id, a.DeviceGeneration, a.CreatedAt, a.RevokedAt)).ToArrayAsync(token);
        var enrollments = await db.AgentEnrollments.AsNoTracking().Where(e => e.DeviceId == deviceId).OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.RowId)
            .Take(50).Select(e => new AgentEnrollmentSummary(e.Id, e.DeviceGeneration, e.CreatedAt, e.ExpiresAt, e.RevokedAt, e.AgentId != null)).ToArrayAsync(token);
        return new(agents, enrollments, generation.Value);
    }

    public async Task<AgentEnrollmentIssue> IssueAsync(Guid deviceId, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) return new(AgentRegistrationStatus.Invalid);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var device = await LockDeviceAsync(db, deviceId, token);
        if (device is null) return new(AgentRegistrationStatus.NotFound);
        if (!Eligible(device)) return new(AgentRegistrationStatus.Conflict);
        var now = clock.GetUtcNow();
        var active = await db.Agents.SingleOrDefaultAsync(a => a.DeviceId == deviceId && a.RevokedAt == null, token);
        if (active is not null && active.DeviceGeneration == device.AgentAuthorizationGeneration) return new(AgentRegistrationStatus.Conflict);
        if (active is not null) active.RevokedAt = now;
        var old = await db.AgentEnrollments.Where(e => e.DeviceId == deviceId && e.RevokedAt == null).ToArrayAsync(token);
        foreach (var enrollment in old) enrollment.RevokedAt = now;
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var enrollment = new AgentEnrollment { Id = Guid.CreateVersion7(), DeviceId = deviceId, DeviceGeneration = device.AgentAuthorizationGeneration,
                Verifier = SHA256.HashData(secret), CreatedAt = now, ExpiresAt = now.AddMinutes(15) };
            db.AgentEnrollments.Add(enrollment);
            await db.SaveChangesAsync(token);

            if (active is not null) await outbox.PublishAsync(db, new AgentRegistrationChanged(active.Id, deviceId, true), token);
            await outbox.PublishAsync(db, new AgentRegistrationAudit(deviceId, active?.Id, actorId, "EnrollmentIssued", now), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
            return new(AgentRegistrationStatus.Success, Encode("vle", enrollment.Id, secret), enrollment.ExpiresAt);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentEnrollmentResult> RedeemAsync(string? value, byte[]? verifier, CancellationToken token)
    {
        if (!TryDecode(value, "vle", out var id, out var secret)) return new(AgentRegistrationStatus.Invalid);
        try
        {
            if (verifier is not { Length: 32 }) return new(AgentRegistrationStatus.Invalid);
            await using var db = await contexts.CreateDbContextAsync(token);
            var binding = await db.AgentEnrollments.AsNoTracking().Where(e => e.Id == id).Select(e => (Guid?)e.DeviceId).SingleOrDefaultAsync(token);
            if (binding is null) return new(AgentRegistrationStatus.Invalid);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var device = await LockDeviceAsync(db, binding.Value, token);
            var enrollment = await db.AgentEnrollments.SingleOrDefaultAsync(e => e.Id == id, token);
            var now = clock.GetUtcNow();
            if (device is null || !Eligible(device) || enrollment is null || enrollment.RevokedAt is not null || enrollment.ExpiresAt <= now ||
                enrollment.DeviceGeneration != device.AgentAuthorizationGeneration || !CryptographicOperations.FixedTimeEquals(enrollment.Verifier, SHA256.HashData(secret)))
                return new(AgentRegistrationStatus.Invalid);
            if (enrollment.AgentId is { } existingId)
            {
                var existing = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == existingId, token);
                return existing is not null && existing.RevokedAt is null && CryptographicOperations.FixedTimeEquals(existing.Verifier, verifier)
                    ? new(AgentRegistrationStatus.Success, existing.Id) : new(AgentRegistrationStatus.Invalid);
            }
            if (await db.Agents.AnyAsync(a => a.DeviceId == device.Id && a.RevokedAt == null, token)) return new(AgentRegistrationStatus.Conflict);
            var agent = new RegisteredAgent { Id = Guid.CreateVersion7(), DeviceId = device.Id, DeviceGeneration = enrollment.DeviceGeneration,
                Verifier = verifier.ToArray(), CreatedAt = now };
            db.Agents.Add(agent);
            enrollment.AgentId = agent.Id;
            await db.SaveChangesAsync(token);

            await outbox.PublishAsync(db, new AgentRegistrationChanged(agent.Id, device.Id, false), token);
            await outbox.PublishAsync(db, new AgentRegistrationAudit(device.Id, agent.Id, null, "AgentEnrolled", now), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
            return new(AgentRegistrationStatus.Success, agent.Id);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<RegisteredAgentIdentity?> AuthenticateAsync(string? credential, CancellationToken token)
    {
        if (!TryDecode(credential, "vla", out var id, out var secret)) return null;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, token);
            if (agent is null || agent.RevokedAt is not null || !CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(secret))) return null;
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == agent.DeviceId, token);
            return device is not null && Eligible(device) && device.AgentAuthorizationGeneration == agent.DeviceGeneration
                ? new(agent.Id, agent.DeviceId, agent.DeviceGeneration) : null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentEnrollmentResult> RecoverAsync(string? enrollmentSecret, string? machineProof, CancellationToken token)
    {
        if (!TryDecode(enrollmentSecret, "vle", out var enrollmentId, out var enrollmentBytes)) return new(AgentRegistrationStatus.Invalid);
        try
        {
            if (!TryDecode(machineProof, "vlr", out var proofId, out var machineBytes)) return new(AgentRegistrationStatus.Invalid);
            try
            {
                if (enrollmentId != proofId) return new(AgentRegistrationStatus.Invalid);
                await using var db = await contexts.CreateDbContextAsync(token);
                var enrollment = await db.AgentEnrollments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == enrollmentId, token);
                if (enrollment is null || enrollment.RevokedAt is not null || enrollment.AgentId is null ||
                    !CryptographicOperations.FixedTimeEquals(enrollment.Verifier, SHA256.HashData(enrollmentBytes))) return new(AgentRegistrationStatus.Invalid);
                var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == enrollment.AgentId, token);
                if (agent is null || agent.RevokedAt is not null || !CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(machineBytes)))
                    return new(AgentRegistrationStatus.Invalid);
                var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == agent.DeviceId, token);
                return device is not null && Eligible(device) && device.AgentAuthorizationGeneration == agent.DeviceGeneration
                    ? new(AgentRegistrationStatus.Success, agent.Id) : new(AgentRegistrationStatus.Invalid);
            }
            finally { CryptographicOperations.ZeroMemory(machineBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(enrollmentBytes); }
    }

    public async Task<AgentRegistrationStatus> BeginRotationAsync(string? credential, byte[]? verifier, CancellationToken token)
    {
        if (!TryDecode(credential, "vla", out var id, out var secret)) return AgentRegistrationStatus.Invalid;
        try
        {
            if (verifier is not { Length: 32 }) return AgentRegistrationStatus.Invalid;
            await using var db = await contexts.CreateDbContextAsync(token);
            var binding = await db.Agents.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.DeviceId).SingleOrDefaultAsync(token);
            if (binding is null) return AgentRegistrationStatus.Invalid;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var device = await LockDeviceAsync(db, binding.Value, token);
            var agent = await db.Agents.SingleAsync(a => a.Id == id, token);
            if (device is null || !Eligible(device) || agent.RevokedAt is not null || agent.DeviceGeneration != device.AgentAuthorizationGeneration ||
                !CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(secret)) || CryptographicOperations.FixedTimeEquals(agent.Verifier, verifier))
                return AgentRegistrationStatus.Invalid;
            var now = clock.GetUtcNow();
            if (agent.PendingVerifier is not null && agent.PendingUntil > now)
                return CryptographicOperations.FixedTimeEquals(agent.PendingVerifier, verifier) ? AgentRegistrationStatus.Success : AgentRegistrationStatus.Conflict;
            agent.PendingVerifier = verifier.ToArray();
            agent.PendingUntil = now.AddMinutes(15);
            await db.SaveChangesAsync(token);

            await outbox.PublishAsync(db, new AgentRegistrationAudit(device.Id, agent.Id, null, "RotationStarted", now), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
            return AgentRegistrationStatus.Success;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentRegistrationStatus> FinishRotationAsync(string? credential, CancellationToken token)
    {
        if (!TryDecode(credential, "vla", out var id, out var secret)) return AgentRegistrationStatus.Invalid;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var binding = await db.Agents.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.DeviceId).SingleOrDefaultAsync(token);
            if (binding is null) return AgentRegistrationStatus.Invalid;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var device = await LockDeviceAsync(db, binding.Value, token);
            var agent = await db.Agents.SingleAsync(a => a.Id == id, token);
            if (device is null || !Eligible(device) || agent.RevokedAt is not null || agent.DeviceGeneration != device.AgentAuthorizationGeneration)
                return AgentRegistrationStatus.Invalid;
            var digest = SHA256.HashData(secret);
            if (agent.PendingVerifier is null && CryptographicOperations.FixedTimeEquals(agent.Verifier, digest)) return AgentRegistrationStatus.Success;
            var now = clock.GetUtcNow();
            if (agent.PendingVerifier is null || agent.PendingUntil <= now || !CryptographicOperations.FixedTimeEquals(agent.PendingVerifier, digest))
                return AgentRegistrationStatus.Invalid;
            agent.Verifier = agent.PendingVerifier;
            agent.PendingVerifier = null;
            agent.PendingUntil = null;
            await db.SaveChangesAsync(token);

            await outbox.PublishAsync(db, new AgentRegistrationAudit(device.Id, agent.Id, null, "RotationFinished", now), token);
            await db.Database.CurrentTransaction!.CommitAsync(token);
            return AgentRegistrationStatus.Success;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentRegistrationStatus> RevokeEnrollmentAsync(Guid enrollmentId, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) return AgentRegistrationStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        var deviceId = await db.AgentEnrollments.AsNoTracking().Where(e => e.Id == enrollmentId).Select(e => (Guid?)e.DeviceId).SingleOrDefaultAsync(token);
        if (deviceId is null) return AgentRegistrationStatus.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await LockDeviceAsync(db, deviceId.Value, token);
        var enrollment = await db.AgentEnrollments.SingleAsync(e => e.Id == enrollmentId, token);
        if (enrollment.RevokedAt is not null) return AgentRegistrationStatus.Success;
        var now = clock.GetUtcNow();
        enrollment.RevokedAt = now;
        await db.SaveChangesAsync(token);

        await outbox.PublishAsync(db, new AgentRegistrationAudit(enrollment.DeviceId, enrollment.AgentId, actorId, "EnrollmentRevoked", now), token);
        await db.Database.CurrentTransaction!.CommitAsync(token);
        return AgentRegistrationStatus.Success;
    }

    public async Task<AgentRegistrationStatus> RevokeAsync(Guid agentId, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) return AgentRegistrationStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        var deviceId = await db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => (Guid?)a.DeviceId).SingleOrDefaultAsync(token);
        if (deviceId is null) return AgentRegistrationStatus.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await LockDeviceAsync(db, deviceId.Value, token);
        var agent = await db.Agents.SingleAsync(a => a.Id == agentId, token);
        if (agent.RevokedAt is not null) return AgentRegistrationStatus.Success;
        var now = clock.GetUtcNow();
        agent.RevokedAt = now;
        await db.SaveChangesAsync(token);

        await outbox.PublishAsync(db, new AgentRegistrationChanged(agent.Id, agent.DeviceId, true), token);
        await outbox.PublishAsync(db, new AgentRegistrationAudit(agent.DeviceId, agent.Id, actorId, "AgentRevoked", now), token);
        await db.Database.CurrentTransaction!.CommitAsync(token);
        return AgentRegistrationStatus.Success;
    }

    private static Task<Device?> LockDeviceAsync(ResourcesDbContext db, Guid id, CancellationToken token) =>
        db.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);

    private static bool Eligible(Device device) => !device.IsArchived && device.IsEnabled && device.DeepMonitoring;
}

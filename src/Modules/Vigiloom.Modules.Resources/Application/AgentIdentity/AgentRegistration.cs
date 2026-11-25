using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using static Vigiloom.Infrastructure.Authentication.AgentCredentials;

namespace Vigiloom.Modules.Resources.Application.AgentIdentity;

/// <summary>Owns device-bound Agent registration and credentials.</summary>
public sealed class AgentRegistration(IDbContextFactory<ResourcesDbContext> contexts, TimeProvider clock, IDbContextOutbox outbox)
{
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
            outbox.Enroll(db);
            if (active is not null) await outbox.PublishAsync(new AgentRegistrationChanged(active.Id, deviceId, true));
            await outbox.PublishAsync(new AgentRegistrationAudit(deviceId, active?.Id, actorId, "EnrollmentIssued", now));
            await outbox.SaveChangesAndFlushMessagesAsync(token);
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
            outbox.Enroll(db);
            await outbox.PublishAsync(new AgentRegistrationChanged(agent.Id, device.Id, false));
            await outbox.PublishAsync(new AgentRegistrationAudit(device.Id, agent.Id, null, "AgentEnrolled", now));
            await outbox.SaveChangesAndFlushMessagesAsync(token);
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
        outbox.Enroll(db);
        await outbox.PublishAsync(new AgentRegistrationChanged(agent.Id, agent.DeviceId, true));
        await outbox.PublishAsync(new AgentRegistrationAudit(agent.DeviceId, agent.Id, actorId, "AgentRevoked", now));
        await outbox.SaveChangesAndFlushMessagesAsync(token);
        return AgentRegistrationStatus.Success;
    }

    private static Task<Device?> LockDeviceAsync(ResourcesDbContext db, Guid id, CancellationToken token) =>
        db.Devices.FromSqlInterpolated($"SELECT * FROM [resources].[Devices] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(token);

    private static bool Eligible(Device device) => !device.IsArchived && device.IsEnabled && device.DeepMonitoring;
}

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public enum AgentIdentityStatus { Success, Invalid, Conflict, NotFound }
public sealed record EnrollmentIssue(AgentIdentityStatus Status, string? Secret = null, DateTimeOffset? ExpiresAt = null);
public sealed record EnrollmentResult(AgentIdentityStatus Status, Guid? AgentId = null);
public sealed record AgentPrincipal(Guid AgentId, Guid DeviceId, long DeviceGeneration);
public sealed record AgentSummary(Guid Id, long DeviceGeneration, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);
public sealed record EnrollmentSummary(Guid Id, long DeviceGeneration, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, bool Consumed);
public sealed record DeviceAgents(IReadOnlyList<AgentSummary> Agents, IReadOnlyList<EnrollmentSummary> Enrollments, long? CurrentGeneration);

/// <summary>Coordinates device-bound enrollment and machine credentials. Callers authorize administrative operations.</summary>
public sealed class AgentIdentity(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices, TimeProvider clock)
{
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);

    public async Task<DeviceAgents?> GetDeviceAgentsAsync(Guid deviceId, CancellationToken token)
    {
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var agents = await db.Agents.AsNoTracking().Where(x => x.DeviceId == deviceId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.RowId)
            .Take(50).Select(x => new AgentSummary(x.Id, x.DeviceGeneration, x.CreatedAt, x.RevokedAt)).ToArrayAsync(token);
        var enrollments = await db.Enrollments.AsNoTracking().Where(x => x.DeviceId == deviceId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.RowId)
            .Take(50).Select(x => new EnrollmentSummary(x.Id, x.DeviceGeneration, x.CreatedAt, x.ExpiresAt, x.RevokedAt, x.AgentId != null)).ToArrayAsync(token);
        return new(agents, enrollments, device.Generation);
    }

    public async Task<EnrollmentIssue> IssueAsync(Guid deviceId, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An authenticated administrator is required.", nameof(actorId));
        var device = await devices.GetAsync(deviceId, token);
        if (device is null) return new(AgentIdentityStatus.NotFound);
        if (!device.Eligible) return new(AgentIdentityStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireAsync(db, deviceId, token);
        if (!await IsCurrentAsync(db, device, token)) return new(AgentIdentityStatus.Conflict);
        var now = clock.GetUtcNow();
        var active = await db.Agents.SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.RevokedAt == null, token);
        if (active is not null && active.DeviceGeneration == device.Generation) return new(AgentIdentityStatus.Conflict);
        if (active is not null)
        {
            active.Revoke(now);
            db.Audits.Add(new AgentAudit(deviceId, active.Id, actorId, "AgentRevoked", now));
        }
        var old = await db.Enrollments.Where(x => x.DeviceId == deviceId && x.RevokedAt == null).ToArrayAsync(token);
        foreach (var item in old) item.Revoke(now);
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var enrollment = new Enrollment(deviceId, device.Generation, SHA256.HashData(secret), now, now + EnrollmentLifetime);
            db.Enrollments.Add(enrollment);
            db.Audits.Add(new AgentAudit(deviceId, null, actorId, "EnrollmentIssued", now));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(AgentIdentityStatus.Success, Encode("vle", enrollment.Id, secret), enrollment.ExpiresAt);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<EnrollmentResult> RedeemAsync(string? enrollmentSecret, byte[]? machineVerifier, CancellationToken token)
    {
        if (!TryDecode(enrollmentSecret, "vle", out var id, out var secret)) return new(AgentIdentityStatus.Invalid);
        try
        {
            if (machineVerifier is not { Length: 32 }) return new(AgentIdentityStatus.Invalid);
            await using var db = await contexts.CreateDbContextAsync(token);
            var binding = await db.Enrollments.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.DeviceId, x.DeviceGeneration }).SingleOrDefaultAsync(token);
            if (binding is null) return new(AgentIdentityStatus.Invalid);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await AgentDeviceLock.AcquireAsync(db, binding.DeviceId, token);
            var enrollment = await db.Enrollments.SingleOrDefaultAsync(x => x.Id == id, token);
            var now = clock.GetUtcNow();
            if (enrollment is null || enrollment.RevokedAt is not null || enrollment.ExpiresAt <= now ||
                !CryptographicOperations.FixedTimeEquals(enrollment.Verifier, SHA256.HashData(secret)) ||
                !await IsCurrentAsync(db, new(binding.DeviceId, binding.DeviceGeneration, true), token))
                return new(AgentIdentityStatus.Invalid);
            if (enrollment.AgentId is { } existingId)
            {
                var existing = await db.Agents.SingleOrDefaultAsync(x => x.Id == existingId, token);
                return existing is not null && existing.RevokedAt is null && CryptographicOperations.FixedTimeEquals(existing.Verifier, machineVerifier)
                    ? new(AgentIdentityStatus.Success, existing.Id) : new(AgentIdentityStatus.Invalid);
            }
            if (await db.Agents.AnyAsync(x => x.DeviceId == binding.DeviceId && x.RevokedAt == null, token))
                return new(AgentIdentityStatus.Conflict);
            var agent = new Agent(binding.DeviceId, binding.DeviceGeneration, machineVerifier.ToArray(), now);
            db.Agents.Add(agent);
            enrollment.Consume(agent.Id);
            db.Audits.Add(new AgentAudit(agent.DeviceId, agent.Id, null, "AgentEnrolled", now));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return new(AgentIdentityStatus.Success, agent.Id);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentPrincipal?> AuthenticateAsync(string? credential, CancellationToken token)
    {
        if (!TryDecode(credential, "vla", out var id, out var secret)) return null;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (agent is null || agent.RevokedAt is not null || !CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(secret))) return null;
            var device = await devices.GetAsync(agent.DeviceId, token);
            return device is { Eligible: true } && device.Generation == agent.DeviceGeneration
                ? new(agent.Id, agent.DeviceId, agent.DeviceGeneration) : null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Record receipt using the current credential and device generation under the same transaction as the write.</summary>
    public Task<AgentIdentityStatus> RecordHeartbeatAsync(string? credential, CancellationToken token) =>
        RecordHeartbeatAsync(credential, null, token);

    public async Task<AgentIdentityStatus> RecordHeartbeatAsync(string? credential, AgentHeartbeatReport? report, CancellationToken token)
    {
        if (report is not null && !report.IsValid())
            throw new ArgumentException("Invalid Agent heartbeat report.", nameof(report));
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return AgentIdentityStatus.Invalid;
        var now = clock.GetUtcNow();
        agent.RecordHeartbeat(now);
        var staleSession = report is not null && agent.QueueReportEpoch != report.Epoch;
        if (report is not null && !staleSession && agent.RecordQueueStatus(report.Sequence, report.Queue, report.Quarantine, now))
        {
            if (report.Configuration is { } settings) agent.RecordConfigurationStatus(settings, now);
            if (report.CapacityConfiguration is { } capacity) agent.RecordQueueCapacityStatus(capacity, report.Sequence, now);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return staleSession ? AgentIdentityStatus.Conflict : AgentIdentityStatus.Success;
    }

    public async Task<AgentQueueSession?> GetQueueSessionAsync(string? credential, CancellationToken token)
    {
        var principal = await AuthenticateAsync(credential, token);
        if (principal is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.Agents.AsNoTracking().Where(x => x.Id == principal.AgentId)
            .Select(x => new AgentQueueSession(x.QueueReportEpoch)).SingleOrDefaultAsync(token);
    }

    public async Task<AgentIdentityStatus> BeginQueueSessionAsync(string? credential, AgentQueueSessionRequest request, CancellationToken token)
    {
        if (request.Epoch.Version != 7 || request.ExpectedEpoch == request.Epoch) return AgentIdentityStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var agent = await AuthenticateForWriteAsync(db, credential, token);
        if (agent is null) return AgentIdentityStatus.Invalid;
        if (agent.QueueReportEpoch == request.Epoch) return AgentIdentityStatus.Success;
        if (agent.QueueReportEpoch != request.ExpectedEpoch) return AgentIdentityStatus.Conflict;
        agent.BeginQueueSession(request.Epoch);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentIdentityStatus.Success;
    }

    /// <summary>Holds device and credential authorization locks until the caller's transaction ends.</summary>
    internal async Task<Agent?> AuthenticateForWriteAsync(AgentManagementDbContext db, string? credential, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Agent writes require an active transaction.");
        if (!TryDecode(credential, "vla", out var id, out var secret)) return null;
        try
        {
            var binding = await db.Agents.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.DeviceId, x.DeviceGeneration }).SingleOrDefaultAsync(token);
            if (binding is null) return null;
            await AgentDeviceLock.AcquireAsync(db, binding.DeviceId, token);
            var agent = await db.Agents.SingleOrDefaultAsync(x => x.Id == id, token);
            return agent is not null && agent.RevokedAt is null &&
                CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(secret)) &&
                await IsCurrentAsync(db, new(binding.DeviceId, binding.DeviceGeneration, true), token) ? agent : null;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>Recover an already-created Agent ID using both the enrollment secret and its machine secret.</summary>
    public async Task<EnrollmentResult> RecoverAsync(string? enrollmentSecret, string? machineProof, CancellationToken token)
    {
        if (!TryDecode(enrollmentSecret, "vle", out var enrollmentId, out var enrollmentBytes)) return new(AgentIdentityStatus.Invalid);
        try
        {
            if (!TryDecode(machineProof, "vlr", out var proofId, out var machineBytes)) return new(AgentIdentityStatus.Invalid);
            try
            {
                if (enrollmentId != proofId) return new(AgentIdentityStatus.Invalid);
                await using var db = await contexts.CreateDbContextAsync(token);
                var enrollment = await db.Enrollments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == enrollmentId, token);
                if (enrollment is null || enrollment.RevokedAt is not null || enrollment.AgentId is null ||
                    !CryptographicOperations.FixedTimeEquals(enrollment.Verifier, SHA256.HashData(enrollmentBytes))) return new(AgentIdentityStatus.Invalid);
                var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == enrollment.AgentId.Value, token);
                if (agent is null || agent.RevokedAt is not null || !CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(machineBytes)))
                    return new(AgentIdentityStatus.Invalid);
                var device = await devices.GetAsync(agent.DeviceId, token);
                return device is { Eligible: true } && device.Generation == agent.DeviceGeneration
                    ? new(AgentIdentityStatus.Success, agent.Id) : new(AgentIdentityStatus.Invalid);
            }
            finally { CryptographicOperations.ZeroMemory(machineBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(enrollmentBytes); }
    }

    public async Task<AgentIdentityStatus> BeginRotationAsync(string? credential, byte[]? newVerifier, CancellationToken token)
    {
        if (!TryDecode(credential, "vla", out var id, out var secret)) return AgentIdentityStatus.Invalid;
        try
        {
            if (newVerifier is not { Length: 32 }) return AgentIdentityStatus.Invalid;
            await using var db = await contexts.CreateDbContextAsync(token);
            var binding = await db.Agents.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.DeviceId, x.DeviceGeneration }).SingleOrDefaultAsync(token);
            if (binding is null) return AgentIdentityStatus.Invalid;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await AgentDeviceLock.AcquireAsync(db, binding.DeviceId, token);
            var agent = await db.Agents.SingleAsync(x => x.Id == id, token);
            if (agent.RevokedAt is not null || !CryptographicOperations.FixedTimeEquals(agent.Verifier, SHA256.HashData(secret)) ||
                !await IsCurrentAsync(db, new(binding.DeviceId, binding.DeviceGeneration, true), token)) return AgentIdentityStatus.Invalid;
            if (CryptographicOperations.FixedTimeEquals(agent.Verifier, newVerifier)) return AgentIdentityStatus.Invalid;
            var now = clock.GetUtcNow();
            if (agent.PendingVerifier is not null && agent.PendingUntil > now)
                return CryptographicOperations.FixedTimeEquals(agent.PendingVerifier, newVerifier) ? AgentIdentityStatus.Success : AgentIdentityStatus.Conflict;
            agent.BeginRotation(newVerifier.ToArray(), now.AddMinutes(15));
            db.Audits.Add(new AgentAudit(agent.DeviceId, agent.Id, null, "RotationStarted", now));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return AgentIdentityStatus.Success;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentIdentityStatus> FinishRotationAsync(string? credential, CancellationToken token)
    {
        if (!TryDecode(credential, "vla", out var id, out var secret)) return AgentIdentityStatus.Invalid;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var binding = await db.Agents.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.DeviceId, x.DeviceGeneration }).SingleOrDefaultAsync(token);
            if (binding is null) return AgentIdentityStatus.Invalid;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await AgentDeviceLock.AcquireAsync(db, binding.DeviceId, token);
            var agent = await db.Agents.SingleAsync(x => x.Id == id, token);
            if (agent.RevokedAt is not null || !await IsCurrentAsync(db, new(binding.DeviceId, binding.DeviceGeneration, true), token))
                return AgentIdentityStatus.Invalid;
            var digest = SHA256.HashData(secret);
            // A lost successful response is safe to retry with the already-committed new credential.
            if (agent.PendingVerifier is null && CryptographicOperations.FixedTimeEquals(agent.Verifier, digest)) return AgentIdentityStatus.Success;
            var now = clock.GetUtcNow();
            if (agent.PendingVerifier is null || agent.PendingUntil <= now || !CryptographicOperations.FixedTimeEquals(agent.PendingVerifier, digest))
                return AgentIdentityStatus.Invalid;
            agent.FinishRotation();
            db.Audits.Add(new AgentAudit(agent.DeviceId, agent.Id, null, "RotationFinished", now));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return AgentIdentityStatus.Success;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<AgentIdentityStatus> RevokeAsync(Guid agentId, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An authenticated administrator is required.", nameof(actorId));
        await using var db = await contexts.CreateDbContextAsync(token);
        var deviceId = await db.Agents.AsNoTracking().Where(x => x.Id == agentId).Select(x => (Guid?)x.DeviceId).SingleOrDefaultAsync(token);
        if (deviceId is null) return AgentIdentityStatus.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireAsync(db, deviceId.Value, token);
        var agent = await db.Agents.SingleAsync(x => x.Id == agentId, token);
        if (agent.RevokedAt is null)
        {
            var now = clock.GetUtcNow();
            agent.Revoke(now);
            db.Audits.Add(new AgentAudit(agent.DeviceId, agent.Id, actorId, "AgentRevoked", now));
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentIdentityStatus.Success;
    }

    public async Task<AgentIdentityStatus> RevokeEnrollmentAsync(Guid enrollmentId, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An authenticated administrator is required.", nameof(actorId));
        await using var db = await contexts.CreateDbContextAsync(token);
        var deviceId = await db.Enrollments.AsNoTracking().Where(x => x.Id == enrollmentId).Select(x => (Guid?)x.DeviceId).SingleOrDefaultAsync(token);
        if (deviceId is null) return AgentIdentityStatus.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireAsync(db, deviceId.Value, token);
        var enrollment = await db.Enrollments.SingleAsync(x => x.Id == enrollmentId, token);
        if (enrollment.RevokedAt is null)
        {
            var now = clock.GetUtcNow();
            enrollment.Revoke(now);
            db.Audits.Add(new AgentAudit(enrollment.DeviceId, enrollment.AgentId, actorId, "EnrollmentRevoked", now));
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentIdentityStatus.Success;
    }

    private async Task<bool> IsCurrentAsync(AgentManagementDbContext db, DeviceAgentState device, CancellationToken token) =>
        await devices.IsCurrentAsync(device.DeviceId, device.Generation, db.Database.GetDbConnection(), db.Database.CurrentTransaction!.GetDbTransaction(), token);

    private static string Encode(string prefix, Guid id, byte[] secret) =>
        $"{prefix}_{id:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static bool TryDecode(string? value, string prefix, out Guid id, out byte[] secret)
    {
        id = default; secret = [];
        var expected = prefix + "_";
        if (value is null || value.Length != expected.Length + 80 || !value.StartsWith(expected, StringComparison.Ordinal) ||
            value[expected.Length + 36] != '.' || !Guid.TryParseExact(value.AsSpan(expected.Length, 36), "D", out id) || id == Guid.Empty) return false;
        try { secret = Convert.FromBase64String(value[(expected.Length + 37)..].Replace('-', '+').Replace('_', '/') + "="); }
        catch (FormatException) { return false; }
        return secret.Length == 32;
    }
}

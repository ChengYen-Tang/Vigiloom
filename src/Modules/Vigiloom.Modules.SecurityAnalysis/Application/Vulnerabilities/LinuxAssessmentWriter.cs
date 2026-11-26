using System.Data;
using System.Security.Cryptography;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Events;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum LinuxWriteResult { Written, AlreadyWritten, Conflict, Changed }

/// <summary>Writes one complete assessment atomically after revalidating all source and inventory fences.</summary>
internal sealed class LinuxAssessmentWriter(IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerFence owners, TimeProvider clock,
    INotificationWriter? notifications = null, IDevicePingSource? devices = null, IDetectionTimelineWriter? timeline = null)
{
    internal const string MatcherVersion = "ubuntu-osv-v4";
    internal const string DebianMatcherVersion = "debian-tracker-v1";

    internal async Task<LinuxWriteResult> WriteAsync(InventoryOwner owner, LinuxPackageAssessment assessment, TimeSpan maximumAge, CancellationToken token)
    {
        if (assessment.Status != LinuxEvaluationStatus.Ready || assessment.Findings is null || assessment.RunId is not { } runId ||
            assessment.SourceId is not { } sourceId || assessment.RevisionId is not { } revisionId ||
            assessment.HostSnapshotId is not { } hostId || assessment.PackageSnapshotId is not { } packageId ||
            string.IsNullOrWhiteSpace(assessment.Ecosystem) || assessment.Ecosystem.Length > 256)
            throw new ArgumentException("A complete assessment is required.", nameof(assessment));
        var digest = LinuxAssessmentDigest.Compute(assessment);
        var matcherVersion = assessment.IsDebian ? DebianMatcherVersion : MatcherVersion;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (!await LinuxAssessmentFence.LockCurrentAsync(db, owners, owner, assessment, clock, maximumAge, token)) return LinuxWriteResult.Changed;
        var receipt = await db.VulnerabilityRuns.SingleOrDefaultAsync(x => x.RunId == runId, token);
        if (receipt is not null)
            return receipt.DeviceId == owner.DeviceId && receipt.SourceId == sourceId && CryptographicOperations.FixedTimeEquals(receipt.Digest, digest)
                ? LinuxWriteResult.AlreadyWritten : LinuxWriteResult.Conflict;
        var coverage = await db.VulnerabilityCoverage.SingleOrDefaultAsync(x => x.DeviceId == owner.DeviceId && x.SourceId == sourceId, token);

        var known = new Dictionary<string, VulnerabilityFinding>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var packages = new HashSet<string>(StringComparer.Ordinal);
        var unknownPackages = new HashSet<string>(StringComparer.Ordinal);
        var now = clock.GetUtcNow().ToUniversalTime();
        var alertSummary = notifications is null ? null : new VulnerabilityNotificationSummary();
        db.VulnerabilityRuns.Add(new VulnerabilityRun { RunId = runId, DeviceId = owner.DeviceId, SourceId = sourceId, Digest = digest, CompletedAt = now,
            RevisionId = revisionId, AgentId = owner.AgentId, DeviceGeneration = owner.DeviceGeneration, HostSnapshotId = hostId,
            PackageSnapshotId = packageId, MatcherVersion = matcherVersion, Ecosystem = assessment.Ecosystem });
        await db.SaveChangesAsync(token);
        foreach (var batch in assessment.Findings.Chunk(128))
        {
            var timelineEntries = timeline is null ? null : new List<EventTimelineEntry>();
            var identities = batch.Where(x => x.AdvisoryId is not null).Select(x => FindingIdentity.Hash(x.PackageKey, x.AdvisoryId!)).ToArray();
            if (identities.Length > 0)
            {
                var parameter = Expression.Parameter(typeof(VulnerabilityFinding), "finding");
                Expression predicate = Expression.Constant(false);
                foreach (var identity in identities)
                {
                    var captured = identity;
                    Expression<Func<byte[]>> value = () => captured;
                    predicate = Expression.OrElse(predicate, Expression.Equal(Expression.Property(parameter, nameof(VulnerabilityFinding.IdentityHash)), value.Body));
                }
                var filter = Expression.Lambda<Func<VulnerabilityFinding, bool>>(predicate, parameter);
                var previous = await db.VulnerabilityFindings.Where(x => x.DeviceId == owner.DeviceId && x.SourceId == sourceId)
                    .Where(filter).ToListAsync(token);
                foreach (var prior in previous) known.Add(Convert.ToHexString(prior.IdentityHash), prior);
            }
            foreach (var result in batch)
            {
            token.ThrowIfCancellationRequested();
            packages.Add(result.PackageKey);
            if (result.State == MatchState.Unknown) unknownPackages.Add(result.PackageKey);
            if (result.AdvisoryId is null)
            {
                if (result.Reason is not (LinuxFindingReason.NoAdvisoryEvidence or LinuxFindingReason.MissingPackageIdentity))
                    throw new InvalidDataException("Unknown package reason is invalid.");
                db.VulnerabilityUnknownPackages.Add(new VulnerabilityUnknownPackage { RunId = runId, PackageKey = result.PackageKey, Reason = (int)result.Reason,
                    SourcePackage = result.SourcePackage, SourceVersion = result.SourceVersion, BinaryVersion = result.BinaryVersion, Architecture = result.Architecture });
                continue;
            }
            if (result.SourcePackage is not { Length: > 0 and <= 256 } || result.SourceVersion is not { Length: > 0 and <= 2048 } ||
                result.BinaryVersion is not { Length: > 0 and <= 2048 } || result.Architecture is not { Length: > 0 and <= 128 })
                throw new InvalidDataException("Finding package evidence is incomplete.");
            var identity = FindingIdentity.Hash(result.PackageKey, result.AdvisoryId);
            var key = Convert.ToHexString(identity);
            if (!seen.Add(key)) throw new InvalidDataException("Duplicate finding identity.");
            if (!known.TryGetValue(key, out var finding))
            {
                finding = new VulnerabilityFinding { DeviceId = owner.DeviceId, SourceId = sourceId, IdentityHash = identity,
                    PackageKey = result.PackageKey, AdvisoryId = result.AdvisoryId, FirstEvaluatedAt = now };
                db.VulnerabilityFindings.Add(finding);
                known.Add(key, finding);
            }
            else if (!string.Equals(finding.PackageKey, result.PackageKey, StringComparison.Ordinal) ||
                     !string.Equals(finding.AdvisoryId, result.AdvisoryId, StringComparison.Ordinal))
                throw new InvalidDataException("Finding identity collision.");
            alertSummary?.Observe(finding, result.State);
            finding.CurrentState = (int)result.State;
            finding.LastEvaluatedAt = now;
            finding.LastPackageSnapshotId = packageId;
            finding.LastRevisionId = revisionId;
            finding.LastRunId = runId;
            db.VulnerabilityEvaluations.Add(new VulnerabilityEvaluation { RunId = runId, FindingId = finding.Id, SourceId = sourceId,
                RevisionId = revisionId, AgentId = owner.AgentId, DeviceGeneration = owner.DeviceGeneration, HostSnapshotId = hostId,
                PackageSnapshotId = packageId, EvaluatedAt = now, MatcherVersion = matcherVersion, SourcePackage = result.SourcePackage,
                SourceVersion = result.SourceVersion, BinaryVersion = result.BinaryVersion, Ecosystem = assessment.Ecosystem,
                Architecture = result.Architecture, State = (int)result.State, Origin = (int)result.Origin, Reason = (int)result.Reason,
                FixStatus = (int?)result.FixStatus, FixedVersion = result.FixedVersion, UbuntuPriority = result.UbuntuPriority });
            timelineEntries?.Add(new("vulnerability", now, owner.DeviceId, "device", $"漏洞評估：{result.AdvisoryId}",
                $"情報版本 {revisionId:D}；狀態 {(int)result.State}", $"/devices/{owner.DeviceId:D}?tab=vulnerabilities&search={Uri.EscapeDataString(result.AdvisoryId)}"));
            }
            await db.SaveChangesAsync(token);
            if (timeline is not null && timelineEntries is { Count: > 0 }) await timeline.WriteAsync(db, timelineEntries, token);
            db.ChangeTracker.Clear();
            known.Clear();
        }
        if (coverage is null)
        {
            coverage = new VulnerabilityCoverage { DeviceId = owner.DeviceId, SourceId = sourceId, MatcherVersion = matcherVersion, Digest = digest };
            db.VulnerabilityCoverage.Add(coverage);
        }
        coverage.RevisionId = revisionId; coverage.RunId = runId; coverage.MatcherVersion = matcherVersion; coverage.Digest = digest;
        coverage.AgentId = owner.AgentId; coverage.DeviceGeneration = owner.DeviceGeneration; coverage.HostSnapshotId = hostId;
        coverage.PackageSnapshotId = packageId; coverage.EvaluatedAt = now; coverage.PackageCount = packages.Count; coverage.UnknownPackageCount = unknownPackages.Count;
        if (db.Entry(coverage).State == EntityState.Detached)
            db.VulnerabilityCoverage.Update(coverage);
        await db.SaveChangesAsync(token);
        if (!await LinuxAssessmentFence.LockCurrentAsync(db, owners, owner, assessment, clock, maximumAge, token)) return LinuxWriteResult.Changed;
        if (notifications is not null && alertSummary is { HasChanges: true })
        {
            var context = devices is null ? null : await devices.GetContextAsync(owner.DeviceId, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
            await notifications.WriteAsync(alertSummary.Create(runId, owner.DeviceId, revisionId, now, context), db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
        }
        await transaction.CommitAsync(token);
        return LinuxWriteResult.Written;
    }
}

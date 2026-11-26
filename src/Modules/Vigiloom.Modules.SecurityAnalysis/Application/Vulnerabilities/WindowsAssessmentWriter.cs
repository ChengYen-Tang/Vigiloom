using System.Buffers.Binary;
using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
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

internal enum WindowsWriteResult { Written, AlreadyWritten, Conflict, Changed }

/// <summary>Publishes product candidates and their observed update evidence atomically.</summary>
internal sealed class WindowsAssessmentWriter(IDbContextFactory<SecurityAnalysisDbContext> contexts, IAgentOwnerFence owners, TimeProvider clock,
    INotificationWriter? notifications = null, IDevicePingSource? devices = null, IDetectionTimelineWriter? timeline = null)
{
    internal const string MatcherVersion = "windows-msrc-v5";

    internal async Task<WindowsWriteResult> WriteAsync(InventoryOwner owner, WindowsProductAssessment assessment, TimeSpan maximumAge, CancellationToken token)
    {
        if (assessment is not { Status: WindowsEvaluationStatus.Ready, HostSnapshotId: { } hostId, RunId: { } runId,
                Product: { Length: > 0 and <= 256 } product, Architecture: { Length: > 0 and <= 128 } architecture,
                Cves: { } cves, Evidence.SourceId: { } sourceId, Evidence.RevisionId: { } revisionId } || runId.Version != 7)
            throw new ArgumentException("A completed Windows assessment is required.", nameof(assessment));
        var ordered = cves.OrderBy(x => x.Cve, StringComparer.Ordinal).ToArray();
        if (ordered.Select(x => x.Cve).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidDataException("Duplicate Windows CVE.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(JsonSerializer.SerializeToUtf8Bytes(new { owner.AgentId, owner.DeviceId, owner.DeviceGeneration, sourceId, revisionId, hostId,
            QuickFixSnapshotId = assessment.QuickFix?.SnapshotId, UpdateSnapshotId = assessment.Updates?.SnapshotId, product, architecture, assessment.CoverageComplete,
            assessment.CatalogComplete, assessment.CatalogCheckedAt }));
        foreach (var cve in ordered) Append(JsonSerializer.SerializeToUtf8Bytes(cve));
        var digest = hash.GetHashAndReset();
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (!await WindowsAssessmentFence.LockCurrentAsync(db, contexts, owners, owner, assessment, clock, maximumAge, token)) return WindowsWriteResult.Changed;
        var receipt = await db.VulnerabilityRuns.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId, token);
        if (receipt is not null)
            return receipt.DeviceId == owner.DeviceId && receipt.SourceId == sourceId && CryptographicOperations.FixedTimeEquals(receipt.Digest, digest)
                ? WindowsWriteResult.AlreadyWritten : WindowsWriteResult.Conflict;
        var now = clock.GetUtcNow().ToUniversalTime();
        var alertSummary = notifications is null ? null : new VulnerabilityNotificationSummary();
        db.VulnerabilityRuns.Add(new VulnerabilityRun { RunId = runId, DeviceId = owner.DeviceId, SourceId = sourceId, Digest = digest, CompletedAt = now,
            RevisionId = revisionId, AgentId = owner.AgentId, DeviceGeneration = owner.DeviceGeneration, HostSnapshotId = hostId,
            MatcherVersion = MatcherVersion, Ecosystem = product, ProductDiscoveryComplete = assessment.CoverageComplete,
            CatalogComplete = assessment.CatalogComplete, CatalogCheckedAt = assessment.CatalogCheckedAt });
        db.VulnerabilityInventoryEvidence.AddRange(
            new VulnerabilityInventoryEvidence { RunId = runId, Source = InventorySource.HostSystem, SnapshotId = hostId, Outcome = InventoryOutcome.Complete,
                ObservedAt = (await WindowsHostInventory.LoadAsync(db, owner, now, maximumAge, token))!.ObservedAt },
            Observation(InventorySource.WindowsQuickFix, assessment.QuickFix), Observation(InventorySource.WindowsUpdate, assessment.Updates));
        await db.SaveChangesAsync(token);
        foreach (var batch in ordered.Chunk(128))
        {
            var timelineEntries = timeline is null ? null : new List<EventTimelineEntry>();
            var identities = batch.Select(x => FindingIdentity.Hash(product, x.Cve)).ToArray();
            var parameter = Expression.Parameter(typeof(VulnerabilityFinding), "finding");
            Expression predicate = Expression.Constant(false);
            foreach (var identity in identities)
            {
                var captured = identity;
                Expression<Func<byte[]>> value = () => captured;
                predicate = Expression.OrElse(predicate, Expression.Equal(Expression.Property(parameter, nameof(VulnerabilityFinding.IdentityHash)), value.Body));
            }
            var filter = Expression.Lambda<Func<VulnerabilityFinding, bool>>(predicate, parameter);
            var existing = await db.VulnerabilityFindings.Where(x => x.DeviceId == owner.DeviceId && x.SourceId == sourceId)
                .Where(filter).ToDictionaryAsync(x => Convert.ToHexString(x.IdentityHash), StringComparer.Ordinal, token);
            foreach (var cve in batch)
            {
                var identity = FindingIdentity.Hash(product, cve.Cve);
                if (!existing.TryGetValue(Convert.ToHexString(identity), out var finding))
                {
                    finding = new VulnerabilityFinding { DeviceId = owner.DeviceId, SourceId = sourceId, IdentityHash = identity,
                        PackageKey = product, AdvisoryId = cve.Cve, FirstEvaluatedAt = now };
                    db.VulnerabilityFindings.Add(finding);
                }
                else if (finding.PackageKey != product || finding.AdvisoryId != cve.Cve) throw new InvalidDataException("Finding identity collision.");
                var detail = JsonSerializer.Serialize(cve);
                if (detail.Length > 8192) throw new InvalidDataException("Windows evidence exceeds the supported size.");
                var state = WindowsPatchCorrelation.Classify(cve);
                alertSummary?.Observe(finding, state);
                finding.CurrentState = (int)state;
                finding.LastEvaluatedAt = now;
                finding.LastPackageSnapshotId = null;
                finding.LastRevisionId = revisionId;
                finding.LastRunId = runId;
                db.VulnerabilityEvaluations.Add(new VulnerabilityEvaluation { RunId = runId, FindingId = finding.Id, SourceId = sourceId,
                    RevisionId = revisionId, AgentId = owner.AgentId, DeviceGeneration = owner.DeviceGeneration, HostSnapshotId = hostId,
                    EvaluatedAt = now, MatcherVersion = MatcherVersion, Ecosystem = product, Architecture = architecture,
                    State = (int)state, Origin = (int)PackageOrigin.Unknown, Reason = 3, WindowsEvidence = detail });
                timelineEntries?.Add(new("vulnerability", now, owner.DeviceId, "device", $"漏洞評估：{cve.Cve}",
                    $"情報版本 {revisionId:D}；狀態 {(int)state}", $"/devices/{owner.DeviceId:D}?tab=vulnerabilities&search={Uri.EscapeDataString(cve.Cve)}"));
            }
            await db.SaveChangesAsync(token);
            if (timeline is not null && timelineEntries is { Count: > 0 }) await timeline.WriteAsync(db, timelineEntries, token);
            db.ChangeTracker.Clear();
        }
        var coverage = await db.VulnerabilityCoverage.SingleOrDefaultAsync(x => x.DeviceId == owner.DeviceId && x.SourceId == sourceId, token);
        if (coverage is null)
        {
            coverage = new VulnerabilityCoverage { DeviceId = owner.DeviceId, SourceId = sourceId, MatcherVersion = MatcherVersion, Digest = digest };
            db.VulnerabilityCoverage.Add(coverage);
        }
        coverage.RevisionId = revisionId; coverage.RunId = runId; coverage.MatcherVersion = MatcherVersion; coverage.Digest = digest;
        coverage.AgentId = owner.AgentId; coverage.DeviceGeneration = owner.DeviceGeneration; coverage.HostSnapshotId = hostId;
        coverage.PackageSnapshotId = null; coverage.EvaluatedAt = now; coverage.PackageCount = 1; coverage.UnknownPackageCount = 1;
        await db.SaveChangesAsync(token);
        if (!await WindowsAssessmentFence.LockCurrentAsync(db, contexts, owners, owner, assessment, clock, maximumAge, token)) return WindowsWriteResult.Changed;
        if (notifications is not null && alertSummary is { HasChanges: true })
        {
            var context = devices is null ? null : await devices.GetContextAsync(owner.DeviceId, db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
            await notifications.WriteAsync(alertSummary.Create(runId, owner.DeviceId, revisionId, now, context), db.Database.GetDbConnection(), transaction.GetDbTransaction(), token);
        }
        await transaction.CommitAsync(token);
        return WindowsWriteResult.Written;

        void Append(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        VulnerabilityInventoryEvidence Observation(InventorySource source, WindowsPatchEvidence? evidence) => new()
        {
            RunId = runId, Source = source, SnapshotId = evidence?.SnapshotId, Outcome = evidence?.Outcome, ObservedAt = evidence?.ObservedAt
        };
    }
}

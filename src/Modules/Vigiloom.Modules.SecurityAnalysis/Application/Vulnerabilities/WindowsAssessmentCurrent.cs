using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Contracts.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Reads the latest retained Windows assessment with source and inventory freshness checks.</summary>
internal sealed class WindowsAssessmentCurrent(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int PageSize = 50;
    internal async Task<CurrentVulnerabilityPage?> GetAsync(InventoryOwner owner, int page, string? search, int? state, string? rankingVersion, CancellationToken token, KevSnapshot? kev = null, bool includeRisk = true)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var sourceIds = await db.IntelligenceSources.AsNoTracking()
            .Where(x => x.Key == MsrcSnapshotStage.SourceKey || includeRisk && (x.Key == EpssSnapshotStage.SourceKey || x.Key == "cisa-kev"))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Key }).ToArrayAsync(token);
        var sourceId = sourceIds.Where(x => x.Key == MsrcSnapshotStage.SourceKey).Select(x => (Guid?)x.Id).SingleOrDefault();
        if (sourceId is null) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var sources = new Dictionary<Guid, IntelligenceSource>();
        foreach (var id in sourceIds.Select(x => x.Id))
        {
            var locked = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
                .AsNoTracking().SingleOrDefaultAsync(token);
            if (locked is not null) sources.Add(id, locked);
        }
        sources.TryGetValue(sourceId.Value, out var source);
        var risk = await RiskSnapshot.LoadAsync(db, sources, includeRisk ? kev : null, clock.GetUtcNow(), token);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        var coverage = await db.VulnerabilityCoverage.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == owner.DeviceId && x.SourceId == sourceId, token);
        if (source is null || coverage is null) return null;
        var run = await db.VulnerabilityRuns.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == coverage.RunId && x.DeviceId == owner.DeviceId && x.SourceId == sourceId, token);
        if (run is null) return null;
        var sourceEvidence = active is null ? null : new MsrcCandidateResult(MsrcCandidateStatus.Ready, [], source.Id, active.RevisionId, source.Version, active.Version);
        var lookup = new MsrcCandidateLookup(contexts, clock);
        var current = sourceEvidence is not null && active!.RevisionId == coverage.RevisionId && coverage.AgentId == owner.AgentId &&
            coverage.DeviceGeneration == owner.DeviceGeneration && coverage.MatcherVersion == WindowsAssessmentWriter.MatcherVersion &&
            await MsrcRevisionEligibility.HasProductIndexAsync(db, coverage.RevisionId, token) && await lookup.IsCurrentAsync(db, sourceEvidence, token);
        if (!await InventoryAgentLock.TryAcquireAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), owner.AgentId, 5000, token)) current = false;
        var now = clock.GetUtcNow();
        var host = await WindowsHostInventory.LoadAsync(db, owner, now, TimeSpan.FromDays(14), token);
        var quick = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsQuickFix, now, TimeSpan.FromDays(14), token);
        var updates = await WindowsPatchInventory.LoadAsync(db, owner, InventorySource.WindowsUpdate, now, TimeSpan.FromDays(14), token);
        var inputs = await db.VulnerabilityInventoryEvidence.AsNoTracking().Where(x => x.RunId == run.RunId).ToDictionaryAsync(x => x.Source, token);
        current &= host?.SnapshotId == coverage.HostSnapshotId &&
            inputs.TryGetValue(InventorySource.WindowsQuickFix, out var quickInput) && quickInput.SnapshotId == quick?.SnapshotId &&
            inputs.TryGetValue(InventorySource.WindowsUpdate, out var updateInput) && updateInput.SnapshotId == updates?.SnapshotId;
        var kevRevision = risk.KevRevision;
        var epssRevision = risk.EpssRevision;
        var query = from finding in db.VulnerabilityFindings.AsNoTracking()
            join evaluation in db.VulnerabilityEvaluations.AsNoTracking() on new { FindingId = finding.Id, RunId = finding.LastRunId }
                equals new { evaluation.FindingId, evaluation.RunId }
            where finding.DeviceId == owner.DeviceId && finding.SourceId == source.Id && finding.LastRunId == coverage.RunId &&
                (state == null || finding.CurrentState == state) &&
                (search == null || EF.Functions.Collate(finding.AdvisoryId, "Latin1_General_100_CI_AS").Contains(search) ||
                    EF.Functions.Collate(finding.PackageKey, "Latin1_General_100_CI_AS").Contains(search))
            select new { finding, evaluation };
        var matching = includeRisk
            ? query.Select(x => new { x.finding, x.evaluation,
                Kev = kevRevision != null && db.KevRecords.Any(k => k.RevisionId == kevRevision && k.CveId == EF.Functions.Collate(x.finding.AdvisoryId, "DATABASE_DEFAULT")),
                Probability = db.EpssScores.Where(e => e.RevisionId == epssRevision && e.Cve == EF.Functions.Collate(x.finding.AdvisoryId, "Latin1_General_100_BIN2"))
                    .Select(e => (decimal?)e.Probability).FirstOrDefault() })
            : query.Select(x => new { x.finding, x.evaluation, Kev = false, Probability = (decimal?)null });
        var rows = await matching.OrderBy(x => x.finding.CurrentState == 0 ? 0 : x.finding.CurrentState == 3 ? 1 : x.finding.CurrentState == 5 ? 2 : x.finding.CurrentState == 4 ? 3 : 4)
            .ThenByDescending(x => x.Kev).ThenByDescending(x => x.Probability).ThenBy(x => x.finding.AdvisoryId).ThenBy(x => x.finding.Id)
            .Select(x => new { x.finding.Id, x.finding.PackageKey, x.finding.AdvisoryId, x.finding.CurrentState, x.finding.LastEvaluatedAt,
                x.evaluation.Origin, x.evaluation.Reason, x.evaluation.BinaryVersion, x.evaluation.WindowsEvidence, x.Kev, x.Probability })
            .Skip((page - 1) * PageSize).Take(PageSize + 1).ToArrayAsync(token);
        var items = rows.Take(PageSize).Select(x => new CurrentVulnerability(x.Id, x.PackageKey, x.AdvisoryId, x.CurrentState,
            x.Origin, x.Reason, x.LastEvaluatedAt, x.BinaryVersion, KnownExploited: kevRevision is null ? null : x.Kev,
            EpssProbability: x.Probability, WindowsDetails: WindowsFindingDetailsReader.Read(x.WindowsEvidence))).ToArray();
        current &= sourceEvidence is not null && await lookup.IsCurrentAsync(db, sourceEvidence, token);
        now = clock.GetUtcNow();
        current &= host is not null && host.ObservedAt <= now.AddMinutes(5) && host.ObservedAt >= now.AddDays(-14) &&
            (quick is null || quick.ObservedAt <= now.AddMinutes(5) && quick.ObservedAt >= now.AddDays(-14)) &&
            (updates is null || updates.ObservedAt <= now.AddMinutes(5) && updates.ObservedAt >= now.AddDays(-14));
        var version = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { owner.AgentId, owner.DeviceId, owner.DeviceGeneration,
            coverage.RunId, source.Version, ActiveVersion = active?.Version, search, state, current, includeRisk,
            risk.KevRevision, KevVersion = risk.KevSource?.Version, KevActivation = risk.Kev?.ActivationVersion,
            risk.EpssRevision, EpssVersion = risk.EpssSource?.Version, risk.EpssActivation })));
        if (page > 1 && !string.Equals(version, rankingVersion, StringComparison.Ordinal))
            return new(VulnerabilityCoverageStatus.Stale, 0, 0, [], page, false, RankingChanged: true);
        if (!await risk.IsFreshAsync(db, clock, token))
            return new(VulnerabilityCoverageStatus.Stale, 0, 0, [], page, false, RankingChanged: true);
        await transaction.CommitAsync(token);
        return new(current ? VulnerabilityCoverageStatus.Current : VulnerabilityCoverageStatus.Stale, coverage.PackageCount, coverage.UnknownPackageCount,
            items, page, rows.Length > PageSize,
            KevStatus: kevRevision is not null ? "Ready" : kev is { Availability: not KevAvailability.Ready } ? kev.Availability.ToString() : "Unavailable",
            KevOrigin: kevRevision is not null ? kev?.Origin : null, KevCheckedAt: kev?.LastSuccessfulCheckAt,
            EpssStatus: epssRevision is null ? "Unavailable" : "Ready", EpssScoredAt: risk.EpssScoredAt,
            RankingVersion: version, WindowsProductDiscoveryComplete: run.ProductDiscoveryComplete,
            WindowsCatalogComplete: run.CatalogComplete == true && run.CatalogCheckedAt is { } checkedAt && checkedAt <= now && checkedAt >= now.AddHours(-23),
            WindowsCatalogCheckedAt: run.CatalogCheckedAt);
    }
}

using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

public enum BaselineRefreshResult { Disabled, NotDue, Busy, Staged, Unchanged, Failed, Conflict }

/// <summary>Fetches only the reviewed upstream versions and stages their mapped checks.</summary>
public sealed class BaselineRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock)
{
    public static IReadOnlyList<string> SourceKeys { get; } = BaselineDefinitions.All.Select(x => x.Key).ToArray();

    public async Task<BaselineRefreshResult> RunDueAsync(string key, CancellationToken token)
    {
        var definition = BaselineDefinitions.Find(key) ?? throw new ArgumentException("Unknown baseline source.", nameof(key));
        var observed = await EnsureSourceAsync(definition, token);
        if (!observed.Enabled) return BaselineRefreshResult.Disabled;
        if (!IntelligenceRefresh.IsDue(observed, clock.GetUtcNow())) return BaselineRefreshResult.NotDue;
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, observed.Id, token);
        if (lease is null) return BaselineRefreshResult.Busy;

        try
        {
            using var client = clients.CreateClient(IntelligenceHttp.RepositoryClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await client.GetAsync(definition.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var content = await BoundedHttpBody.ReadAsync(response.Content, Math.Min(observed.MaximumBytes, 2 * 1024 * 1024), timeout.Token);
            if (!BaselineDefinitions.Matches(definition, content)) throw new InvalidDataException("Source content differs from the reviewed version.");
            return await SaveAsync(observed, definition, null, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            return await SaveAsync(observed, definition, "Baseline source retrieval or validation failed.", token);
        }
    }

    private async Task<IntelligenceSource> EnsureSourceAsync(BaselineDefinition definition, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = {definition.Key}").SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = definition.Key, Enabled = true };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }

    private async Task<BaselineRefreshResult> SaveAsync(IntelligenceSource observed, BaselineDefinition definition, string? error, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {observed.Id}").SingleOrDefaultAsync(token);
        if (source is null || !source.Enabled || !source.Version.AsSpan().SequenceEqual(observed.Version)) return BaselineRefreshResult.Conflict;
        source.LastCheckedAt = clock.GetUtcNow();
        source.LastError = error;
        if (error is not null)
        {
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return BaselineRefreshResult.Failed;
        }

        var artifact = definition.Artifact;
        var digest = SHA256.HashData(artifact);
        var revision = await db.IntelligenceRevisions.SingleOrDefaultAsync(x => x.SourceId == source.Id && x.Digest == digest && x.MappingVersion == BaselineDefinitions.Mapping, token);
        if (revision is { Revoked: true } || await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == source.Id && x.Digest == digest && x.Revoked, token))
        {
            source.LastError = "The reviewed baseline revision is revoked.";
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return BaselineRefreshResult.Failed;
        }
        var staged = revision is null;
        if (revision is null)
        {
            revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = definition.Commit, RetrievedAt = clock.GetUtcNow(),
                Artifact = artifact, Digest = digest, SchemaVersion = BaselineDefinitions.Schema, MappingVersion = BaselineDefinitions.Mapping,
                LicenseEvidence = definition.LicenseEvidence, RecordCount = 1, Normalized = true };
            db.IntelligenceRevisions.Add(revision);
        }
        else if (!BaselineEligibility.IsEligible(definition, revision))
        {
            source.LastError = "The reviewed baseline revision is incomplete.";
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return BaselineRefreshResult.Failed;
        }
        source.LastObservedRevisionId = revision.Id;
        source.LastSuccessfulCheckAt = source.LastCheckedAt;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return staged ? BaselineRefreshResult.Staged : BaselineRefreshResult.Unchanged;
    }
}

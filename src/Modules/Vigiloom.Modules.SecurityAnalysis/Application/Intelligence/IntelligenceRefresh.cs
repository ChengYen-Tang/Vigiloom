using System.Buffers.Binary;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

public enum IntelligenceRefreshResult { NotDue, Disabled, Staged, Unchanged, Failed, Conflict, Busy }

/// <summary>Ensures the built-in source exists and refreshes it when its central schedule is due.</summary>
public sealed class IntelligenceRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock)
{
    public async Task<IntelligenceRefreshResult> RunDueAsync(CancellationToken token)
    {
        var source = await EnsureSourceAsync(token);
        if (!source.Enabled) return IntelligenceRefreshResult.Disabled;
        if (!IsDue(source, clock.GetUtcNow())) return IntelligenceRefreshResult.NotDue;
        return await new KevUpdate(contexts, clients, clock).RunAsync(source.Id, token, onlyWhenDue: true) switch
        {
            KevUpdateResult.Staged => IntelligenceRefreshResult.Staged,
            KevUpdateResult.Unchanged => IntelligenceRefreshResult.Unchanged,
            KevUpdateResult.Conflict => IntelligenceRefreshResult.Conflict,
            KevUpdateResult.Busy => IntelligenceRefreshResult.Busy,
            KevUpdateResult.NotDue => IntelligenceRefreshResult.NotDue,
            KevUpdateResult.Unavailable => IntelligenceRefreshResult.Disabled,
            _ => IntelligenceRefreshResult.Failed
        };
    }

    internal static bool IsDue(IntelligenceSource source, DateTimeOffset now)
    {
        if (source.LastCheckedAt is not { } last) return true;
        Span<byte> bytes = stackalloc byte[16];
        source.Id.TryWriteBytes(bytes);
        var jitter = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) % 301;
        var delay = source.LastError is null ? source.UpdateIntervalSeconds : Math.Min(source.UpdateIntervalSeconds, 900);
        return now >= last.AddSeconds(delay + jitter);
    }

    internal async Task<IntelligenceSource> EnsureSourceAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources
            .FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = N'cisa-kev'")
            .SingleOrDefaultAsync(token);
        if (source is null)
        {
            source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            db.IntelligenceSources.Add(source);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return source;
    }
}

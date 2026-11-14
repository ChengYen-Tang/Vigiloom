using System.Buffers;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

public enum KevOfflineImportResult { Imported, Invalid, Unavailable, Busy, Conflict }

/// <summary>Imports an administrator-supplied KEV-format snapshot without asserting upstream authenticity.</summary>
public sealed class KevOfflineImport(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public async Task<KevOfflineImportResult> ImportAsync(Guid sourceId, Stream input, Guid actorId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (sourceId == Guid.Empty || actorId == Guid.Empty || !input.CanRead) throw new ArgumentException("Invalid import input.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var limit = await db.IntelligenceSources.AsNoTracking().Where(x => x.Id == sourceId && x.Key == "cisa-kev" && x.Enabled)
            .Select(x => (int?)x.MaximumBytes).SingleOrDefaultAsync(token);
        if (limit is null) return KevOfflineImportResult.Unavailable;
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return KevOfflineImportResult.Busy;
        var maximumBytes = Math.Min(limit.Value, 64 * 1024 * 1024);
        if (input.CanSeek)
        {
            var remaining = input.Length - input.Position;
            if (remaining <= 0 || remaining > maximumBytes) return KevOfflineImportResult.Invalid;
            var content = GC.AllocateUninitializedArray<byte>(checked((int)remaining));
            try { await input.ReadExactlyAsync(content, token); }
            catch (EndOfStreamException) { return KevOfflineImportResult.Invalid; }
            return await ImportContentAsync(sourceId, content, actorId, token);
        }
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) != 0)
            {
                if (output.Length + read > maximumBytes) return KevOfflineImportResult.Invalid;
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (output.Length == 0) return KevOfflineImportResult.Invalid;
        return await ImportContentAsync(sourceId, output.ToArray(), actorId, token);
    }

    private async Task<KevOfflineImportResult> ImportContentAsync(Guid sourceId, byte[] content, Guid actorId, CancellationToken token) =>
        await new KevUpdate(contexts, null, clock).ImportAsync(sourceId, content, actorId, audit, token) switch
        {
            KevUpdateResult.Staged or KevUpdateResult.Unchanged => KevOfflineImportResult.Imported,
            KevUpdateResult.Failed => KevOfflineImportResult.Invalid,
            KevUpdateResult.Busy => KevOfflineImportResult.Busy,
            KevUpdateResult.Conflict => KevOfflineImportResult.Conflict,
            _ => KevOfflineImportResult.Unavailable
        };
}

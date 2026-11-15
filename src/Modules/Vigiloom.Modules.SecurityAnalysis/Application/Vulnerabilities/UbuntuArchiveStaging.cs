namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Stages a validated archive in bounded batches; the caller controls publication of the revision.</summary>
internal static class UbuntuArchiveStaging
{
    internal static async Task<int> StageAsync(Stream archive, UbuntuRevisionWriter writer, long maximumExpandedBytes,
        int maximumEntries, int maximumDocumentBytes, long maximumScopes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (maximumScopes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumScopes));
        var batch = new List<UbuntuPreparedDocument>(UbuntuRevisionWriter.MaximumBatchDocuments);
        long scopeCount = 0;
        var batchScopes = 0;
        long batchBytes = 0;
        var count = await UbuntuOsvArchive.ReadAsync(archive, maximumExpandedBytes, maximumEntries, maximumDocumentBytes,
            async (record, raw, cancellation) =>
            {
                var document = await UbuntuOsvDocument.CreateAsync(record, raw, cancellation);
                if (document.Scopes.Length > maximumScopes - scopeCount || document.Scopes.Length > UbuntuRevisionWriter.MaximumBatchScopes ||
                    document.Compressed.Length > UbuntuRevisionWriter.MaximumBatchBytes)
                    throw new InvalidDataException("Ubuntu document scopes or compressed content exceed staging limits.");
                if (batch.Count > 0 && (batch.Count == UbuntuRevisionWriter.MaximumBatchDocuments ||
                    batchScopes + document.Scopes.Length > UbuntuRevisionWriter.MaximumBatchScopes ||
                    batchBytes + document.Compressed.Length > UbuntuRevisionWriter.MaximumBatchBytes))
                    await FlushAsync(cancellation);
                batch.Add(new(record.Id, document));
                scopeCount += document.Scopes.Length;
                batchScopes += document.Scopes.Length;
                batchBytes += document.Compressed.Length;
            }, token);
        if (batch.Count > 0) await FlushAsync(token);
        return count;

        async Task FlushAsync(CancellationToken cancellation)
        {
            await writer.WriteAsync(batch, cancellation);
            batch.Clear();
            batchScopes = 0;
            batchBytes = 0;
        }
    }
}

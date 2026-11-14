using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

public enum SignatureRefreshResult { Disabled, NotDue, Busy, Staged, Unchanged, Failed, Conflict }

/// <summary>Stages source files without enabling detection or publication.</summary>
public sealed class SignatureRefresh(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock)
{
    private const string SourceKey = "signature-base";
    internal const string MappingVersion = "signature-base-source-v2";
    internal const string SchemaVersion = "signature-base-files-v1";
    internal const string LicenseEvidence = "Signature-Base repository terms and per-rule exceptions require review; Neo23x0/signature-base@eb8c53cb08ed103d031fe9c98d1d23e94904839d";

    private static readonly RepositoryRefreshSpec Spec = new(SourceKey, MappingVersion, SchemaVersion, LicenseEvidence,
        "Signature-Base", RepositorySource.SignatureBase,
        (db, revision, token) => db.SignatureFiles.CountAsync(x => x.RevisionId == revision, token),
        StageAsync, (commit, hash, count) => JsonSerializer.SerializeToUtf8Bytes(new { format = "vigiloom-signature-base-source-v1", commit, archiveSha256 = Convert.ToHexStringLower(hash), mapping = MappingVersion, fileCount = count, coverage = "IOC,YARA; interpretation and execution unsupported" }),
        error => error is SignatureLicenseException);

    public async Task<SignatureRefreshResult> RunDueAsync(CancellationToken token) =>
        (SignatureRefreshResult)await new RepositoryRefreshRunner(contexts, clients, clock, Spec).RunDueAsync(token);

    private static async Task<int> StageAsync(SecurityAnalysisDbContext db, Guid revisionId, Stream file,
        string commit, int maximumFiles, CancellationToken token)
    {
        var pending = 0;
        var totalLines = 0;
        var count = await SignatureArchive.ReadAsync(file, commit, maximumFiles, async (filePath, kind, content, digest, cancellation) =>
        {
            var sourceFile = new SignatureFile { RevisionId = revisionId, Path = filePath, Kind = (int)kind,
                Digest = digest, Content = content };
            db.SignatureFiles.Add(sourceFile);
            if (kind == SignatureFileKind.Ioc)
            {
                await db.SaveChangesAsync(cancellation);
                foreach (var entry in db.ChangeTracker.Entries<SignatureFile>().ToArray()) entry.State = EntityState.Detached;
                pending = 0;
                var pendingLines = 0;
                foreach (var line in IocParser.Parse(filePath, content))
                {
                    if (++totalLines > 500_000) throw new InvalidDataException("IOC snapshot contains too many entries.");
                    db.IocEntries.Add(new IocEntry { SignatureFileRowId = sourceFile.RowId, LineNumber = line.Number,
                        Kind = line.Kind, Value = line.Value, Reason = line.Reason });
                    if (++pendingLines < 128) continue;
                    await db.SaveChangesAsync(cancellation);
                    foreach (var entry in db.ChangeTracker.Entries<IocEntry>().ToArray()) entry.State = EntityState.Detached;
                    pendingLines = 0;
                }
                if (pendingLines != 0) await db.SaveChangesAsync(cancellation);
                foreach (var entry in db.ChangeTracker.Entries<IocEntry>().ToArray()) entry.State = EntityState.Detached;
                return;
            }
            if (++pending < 16) return;
            await db.SaveChangesAsync(cancellation);
            foreach (var entry in db.ChangeTracker.Entries<SignatureFile>().ToArray()) entry.State = EntityState.Detached;
            pending = 0;
        }, token);
        if (pending != 0) await db.SaveChangesAsync(token);
        return count;
    }
}

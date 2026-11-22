using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IocHostLogTests(TestContext context)
{
    [TestMethod]
    public Task MatchingEvent_IsStoredWithSourceAndOriginalEvent() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new IntelligenceSource { Key = "signature-base", Enabled = true, IocDetectionEnabled = true, LastSuccessfulCheckAt = now };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = new string('a', 40), RetrievedAt = now,
                Digest = new byte[32], Artifact = [1], SchemaVersion = "1", MappingVersion = "1", LicenseEvidence = "test", Normalized = true };
            var activation = new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = now };
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.IntelligenceSources.Add(source); db.IntelligenceRevisions.Add(revision); db.IntelligenceActivations.Add(activation);
                await db.SaveChangesAsync(token);
            }
            var agent = Guid.CreateVersion7(); var device = Guid.CreateVersion7(); var id = Guid.CreateVersion7();
            const string xml = """
                <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><EventID>3</EventID><Channel>Microsoft-Windows-Sysmon/Operational</Channel></System><EventData><Data Name="DestinationIp">192.0.2.1</Data></EventData></Event>
                """;
            var log = new HostLogEntry(agent, id, Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent, "Sysmon", 1, now, now, xml, null, 3, null, null, null, null);
            var selection = new IocSourceSnapshot(IocSourceAvailability.Ready, source.Id, revision.Id, Convert.ToBase64String(activation.Version), revision.UpstreamVersion, "test", 1, 0);
            var handler = new IocHostLogHandler(new Source(selection), new Logs(log), TimeProvider.System, NullLogger<IocHostLogHandler>.Instance);
            var dispatch = new HostLogDispatch(agent, device, 0, [id]);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                var message = new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.IocMessageType, JsonSerializer.Serialize(dispatch));
                var unavailable = new IocHostLogHandler(new Source(selection with { Availability = IocSourceAvailability.Stale }), new Logs(log), TimeProvider.System, NullLogger<IocHostLogHandler>.Instance);
                await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.HandleAsync(message, connection, transaction, token));
                await handler.HandleAsync(message, connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            await using var verify = new SecurityAnalysisDbContext(options);
            var match = await verify.IocDetections.SingleAsync(token);
            Assert.AreEqual(id, match.MessageId);
            Assert.AreEqual(revision.Id, match.RevisionId);
            Assert.AreEqual("192.0.2.1", match.Value);
            Assert.AreEqual("DestinationIp", match.Field);
        }, context.CancellationToken);

    private sealed class Logs(HostLogEntry log) : IHostLogReader
    {
        public Task<IReadOnlyList<HostLogEntry>> ReadAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<HostLogEntry>>([log]);
    }

    private sealed class Source(IocSourceSnapshot selection) : IIocSource
    {
        public Task<IocSourceSnapshot> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(selection);
        public Task<IocSourceFile> ReadFileAsync(IocSourceSnapshot snapshot, string path, CancellationToken cancellationToken) =>
            Task.FromResult(new IocSourceFile(IocSourceAvailability.Ready, path, null, null, null, null, null,
                path.Contains("c2", StringComparison.Ordinal) ? "192.0.2.1"u8.ToArray() : []));
    }
}

using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdateOutcomeTests(TestContext context)
{
    [TestMethod]
    public async Task TerminalOutcomesSurviveLaterUpdatesAndAcknowledgementIsAgentScoped()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"update-outcomes-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var agent = Guid.CreateVersion7();
            var other = Guid.CreateVersion7();
            var update = Guid.CreateVersion7();
            var target = new UpdateProvenance(agent, Guid.CreateVersion7(), new string('a', 64));
            var now = DateTimeOffset.UtcNow;
            var steps = new[] { new UpdateStep(UpdatePhase.Prepared, now, null), new UpdateStep(UpdatePhase.RolledBack, now, "Interrupted") };
            var first = new UpdateState(update, Path.Combine(directory, "old"), Path.Combine(directory, "new"), UpdatePhase.RolledBack,
                now, steps, Error: "Interrupted", RetryAllowed: true, Provenance: target);
            var journal = new UpdateJournal(directory);
            await journal.WriteAsync(first, context.CancellationToken);
            await journal.WriteAsync(first, context.CancellationToken);
            var pending = await new UpdateJournal(directory).ReadPendingAsync(agent, 10, context.CancellationToken);
            Assert.HasCount(1, pending);
            Assert.AreEqual(UpdateFailure.Interrupted, pending[0].Failure);
            Assert.AreEqual(7, pending[0].MessageId.Version);
            var message = pending[0].MessageId;
            await journal.AcknowledgeAsync(other, message, context.CancellationToken);
            Assert.HasCount(1, await journal.ReadPendingAsync(agent, 10, context.CancellationToken));

            var success = first with { Phase = UpdatePhase.Succeeded, Error = null, RetryAllowed = false,
                UpdatedAt = now.AddSeconds(1), Steps = [.. steps, new(UpdatePhase.Succeeded, now.AddSeconds(1), null)] };
            await journal.WriteAsync(success, context.CancellationToken);
            await journal.WriteAsync(new UpdateState(Guid.CreateVersion7(), first.PreviousDirectory, first.TargetDirectory,
                UpdatePhase.Prepared, now.AddSeconds(2), [new(UpdatePhase.Prepared, now.AddSeconds(2), null)], Provenance: target), context.CancellationToken);
            pending = await new UpdateJournal(directory).ReadPendingAsync(agent, 10, context.CancellationToken);
            Assert.HasCount(2, pending);
            Assert.AreEqual(message, pending[0].MessageId);
            Assert.AreEqual(UpdatePhase.Succeeded, pending[1].Phase);
            Assert.IsGreaterThan(pending[0].Sequence, pending[1].Sequence);
            await journal.AcknowledgeAsync(agent, message, context.CancellationToken);
            await journal.WriteAsync(first, context.CancellationToken);
            pending = await new UpdateJournal(directory).ReadPendingAsync(agent, 10, context.CancellationToken);
            Assert.HasCount(1, pending);
            Assert.AreEqual(UpdatePhase.Succeeded, pending[0].Phase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task OutboxFailureRollsBackTerminalJournalWrite()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"update-outcomes-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var target = new UpdateProvenance(Guid.CreateVersion7(), Guid.CreateVersion7(), new string('b', 64));
            var state = new UpdateState(Guid.CreateVersion7(), Path.Combine(directory, "old"), Path.Combine(directory, "new"),
                UpdatePhase.Prepared, now, [new(UpdatePhase.Prepared, now, null)], Provenance: target);
            var journal = new UpdateJournal(directory);
            await journal.WriteAsync(state, context.CancellationToken);
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "update.db")};Pooling=False"))
            {
                await db.OpenAsync(context.CancellationToken);
                await using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER FailOutcome BEFORE INSERT ON UpdateOutbox BEGIN SELECT RAISE(ABORT, 'injected failure'); END";
                await command.ExecuteNonQueryAsync(context.CancellationToken);
            }
            var terminal = state with { Phase = UpdatePhase.Succeeded, Steps = [.. state.Steps, new(UpdatePhase.Succeeded, now, null)] };
            await Assert.ThrowsAsync<SqliteException>(() => journal.WriteAsync(terminal, context.CancellationToken));
            Assert.AreEqual(UpdatePhase.Prepared, (await journal.ReadAsync(context.CancellationToken))!.Phase);
            Assert.IsEmpty(await journal.ReadPendingAsync(target.AgentId, 10, context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task OlderServerDoesNotAcknowledgePendingOutcome()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"update-outcomes-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var agentId = Guid.CreateVersion7();
            var target = new UpdateProvenance(agentId, Guid.CreateVersion7(), new string('d', 64));
            var state = new UpdateState(Guid.CreateVersion7(), Path.Combine(directory, "old"), Path.Combine(directory, "new"),
                UpdatePhase.Succeeded, now, [new(UpdatePhase.Succeeded, now, null)], Provenance: target);
            var journal = new UpdateJournal(directory);
            await journal.WriteAsync(state, context.CancellationToken);
            var before = (await journal.ReadPendingAsync(agentId, 10, context.CancellationToken)).Single();
            using var http = new HttpClient(new OlderServerHandler());
            var credential = $"vla_{agentId:D}.{new string('a', 43)}";
            Assert.AreEqual(0, await new UpdateResultDelivery(http, journal, TimeProvider.System)
                .FlushAsync(new Uri("https://example.test/"), credential, context.CancellationToken));
            var pending = await journal.ReadPendingAsync(agentId, 10, context.CancellationToken);
            Assert.HasCount(1, pending);
            Assert.AreEqual(before.MessageId, pending[0].MessageId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task UnsupportedProgressDoesNotBlockDurableTerminalResult()
    {
        var directory = Path.Combine(context.TestRunDirectory!, $"update-progress-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var agentId = Guid.CreateVersion7();
            var updateId = Guid.CreateVersion7();
            var messageId = Guid.CreateVersion7();
            var target = new UpdateProvenance(agentId, Guid.CreateVersion7(), new string('a', 64));
            var step = new UpdateStep(UpdatePhase.Switching, now, null, messageId);
            var state = new UpdateState(updateId, "old", "new", UpdatePhase.Switching, now, [step], Provenance: target);
            var journal = new UpdateJournal(directory);
            await journal.WriteAsync(state, context.CancellationToken);
            var handler = new LegacyProgressHandler();
            using var http = new HttpClient(handler);
            var delivery = new UpdateResultDelivery(http, journal, TimeProvider.System);
            var credential = $"vla_{agentId:D}.{new string('a', 43)}";
            var server = new Uri("https://example.test/");
            await delivery.ReportProgressAsync(server, credential, context.CancellationToken);
            await delivery.ReportProgressAsync(server, credential, context.CancellationToken);
            Assert.HasCount(2, handler.Reports);
            Assert.AreEqual(AgentUpdateResultKind.Switching, handler.Reports[0].Result);
            Assert.AreEqual(messageId, handler.Reports[0].MessageId);
            Assert.AreEqual(handler.Reports[0], handler.Reports[1]);

            var terminal = state with { Phase = UpdatePhase.Succeeded, UpdatedAt = now.AddSeconds(1),
                Steps = [step, new(UpdatePhase.Succeeded, now.AddSeconds(1), null, Guid.CreateVersion7())] };
            await journal.WriteAsync(terminal, context.CancellationToken);
            await delivery.ReportProgressAsync(server, credential, context.CancellationToken);
            Assert.HasCount(2, handler.Reports);
            Assert.AreEqual(1, await delivery.FlushAsync(server, credential, context.CancellationToken));
            Assert.AreEqual(AgentUpdateResultKind.Succeeded, handler.Reports[2].Result);
            Assert.IsEmpty(await journal.ReadPendingAsync(agentId, 10, context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class LegacyProgressHandler : HttpMessageHandler
    {
        public List<AgentUpdateResult> Reports { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var report = await request.Content!.ReadFromJsonAsync<AgentUpdateResult>(token);
            Assert.IsNotNull(report);
            Reports.Add(report);
            return new(report.Result == AgentUpdateResultKind.Succeeded ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest);
        }
    }

    private sealed class OlderServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

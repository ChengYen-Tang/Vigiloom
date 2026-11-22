using Vigiloom.Updater.Core.Updates;
using Microsoft.Data.Sqlite;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdateCoordinatorTests(TestContext context)
{
    private static Task<UpdateAuthorization> Authorized(Guid _, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(UpdateAuthorization.Authorized);
    }

    [TestMethod]
    public async Task ChangedAssignmentBeforeStopLeavesAgentRunningAndCanBeRetried()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime();
            var journal = new UpdateJournal(directory);
            var coordinator = new UpdateCoordinator(journal, runtime);
            var id = Guid.CreateVersion7();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(id, "old", "new",
                (_, _) => Task.FromResult(UpdateAuthorization.NoLongerAuthorized), context.CancellationToken));
            Assert.IsEmpty(runtime.Actions);
            Assert.IsNull(await journal.ReadAsync(context.CancellationToken));
            Assert.AreEqual(UpdatePhase.Succeeded, (await coordinator.ApplyAsync(id, "old", "new", Authorized, context.CancellationToken)).Phase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ChangedAssignmentDuringStopPreservesStoppedState()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime();
            var checks = 0;
            var coordinator = new UpdateCoordinator(new UpdateJournal(directory), runtime);
            var result = await coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "new",
                (_, _) => Task.FromResult(++checks == 1 ? UpdateAuthorization.Authorized : UpdateAuthorization.NoLongerAuthorized), context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Failed, result.Phase);
            Assert.AreEqual("old", await runtime.GetSelectedDirectoryAsync(context.CancellationToken));
            Assert.DoesNotContain("select:new", runtime.Actions);
            CollectionAssert.AreEqual(new[] { "stop" }, runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task AuthorizationUnavailableBeforeStopDoesNotConsumeAssignment()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime();
            var journal = new UpdateJournal(directory);
            var coordinator = new UpdateCoordinator(journal, runtime);
            var id = Guid.CreateVersion7();
            await Assert.ThrowsExactlyAsync<UpdateAuthorizationUnavailableException>(() => coordinator.ApplyAsync(id, "old", "new",
                (_, _) => Task.FromResult(UpdateAuthorization.Unavailable), context.CancellationToken));
            Assert.IsEmpty(runtime.Actions);
            Assert.IsNull(await journal.ReadAsync(context.CancellationToken));
            Assert.AreEqual(UpdatePhase.Succeeded, (await coordinator.ApplyAsync(id, "old", "new", Authorized, context.CancellationToken)).Phase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task TemporaryAuthorizationFailureAfterStopWaitsForAdministrator()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime();
            var checks = 0;
            var coordinator = new UpdateCoordinator(new UpdateJournal(directory), runtime);
            var id = Guid.CreateVersion7();
            var result = await coordinator.ApplyAsync(id, "old", "new", (_, _) => ++checks == 1
                ? Task.FromResult(UpdateAuthorization.Authorized)
                : Task.FromException<UpdateAuthorization>(new HttpRequestException()), context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Failed, result.Phase);
            Assert.AreEqual("old", await runtime.GetSelectedDirectoryAsync(context.CancellationToken));
            Assert.AreEqual(result.Phase, (await coordinator.ApplyAsync(id, "old", "new", Authorized, context.CancellationToken)).Phase);
            CollectionAssert.AreEqual(new[] { "stop" }, runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task AuthorizationFailureIsDurablyReportedWithoutRollback()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime();
            var coordinator = new UpdateCoordinator(new UpdateJournal(directory), runtime);
            var id = Guid.CreateVersion7();
            var provenance = new UpdateProvenance(Guid.CreateVersion7(), Guid.CreateVersion7(), new string('c', 64));
            var checks = 0;
            var first = await coordinator.ApplyAsync(id, "old", "new", (_, _) => Task.FromResult(++checks == 1
                ? UpdateAuthorization.Authorized : UpdateAuthorization.Unavailable), context.CancellationToken, provenance);
            Assert.AreEqual(UpdatePhase.Failed, first.Phase);
            var recovered = await new UpdateCoordinator(new UpdateJournal(directory), runtime).RecoverAsync(context.CancellationToken);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(UpdatePhase.Failed, recovered.Phase);
            Assert.IsFalse(recovered.RetryAllowed);
            var pending = await new UpdateJournal(directory).ReadPendingAsync(provenance.AgentId, 10, context.CancellationToken);
            Assert.HasCount(1, pending);
            Assert.AreEqual(UpdatePhase.Failed, pending[0].Phase);
            Assert.AreEqual(AgentUpdateResultKind.Failed, pending[0].ToReport().Result);
            CollectionAssert.AreEqual(new[] { "stop" }, runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task SuccessfulUpdateRecordsNewVersionAfterHealthCheck()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime();
            var journal = new UpdateJournal(directory);
            var coordinator = new UpdateCoordinator(journal, runtime);
            var id = Guid.CreateVersion7();
            var result = await coordinator.ApplyAsync(id, "old", "new", Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Succeeded, result.Phase);
            Assert.AreEqual("new", runtime.Selected);
            CollectionAssert.AreEqual(new[] { "stop", "select:new", "start", "health" }, runtime.Actions);
            var saved = await journal.ReadAsync(context.CancellationToken);
            Assert.IsNotNull(saved);
            Assert.AreEqual(result.Id, saved.Id);
            Assert.AreEqual(result.Phase, saved.Phase);
            CollectionAssert.AreEqual(result.Steps.ToArray(), saved.Steps.ToArray());
            // Startup recovery uses the local journal and must leave a completed update running.
            var recovered = await new UpdateCoordinator(new UpdateJournal(directory), runtime).RecoverAsync(context.CancellationToken);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(UpdatePhase.Succeeded, recovered.Phase);
            Assert.AreEqual("new", runtime.Selected);
            Assert.HasCount(4, runtime.Actions);
            Assert.AreEqual(result.Phase, (await coordinator.ApplyAsync(id, "old", "new", Authorized, context.CancellationToken)).Phase);
            Assert.HasCount(4, runtime.Actions);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(id, "old", "different", Authorized, context.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task UnhealthyVersionRemainsSelectedForInvestigation()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime { Healthy = version => version == "old" };
            var journal = new UpdateJournal(directory);
            var result = await new UpdateCoordinator(journal, runtime).ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Failed, result.Phase);
            Assert.AreEqual("new", runtime.Selected);
            CollectionAssert.AreEqual(new[] { "stop", "select:new", "start", "health" }, runtime.Actions);
            var saved = await journal.ReadAsync(context.CancellationToken);
            Assert.IsNotNull(saved);
            Assert.AreEqual(result.Phase, saved.Phase);
            CollectionAssert.AreEqual(result.Steps.ToArray(), saved.Steps.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task InterruptedSwitchIsPreservedWithoutCentralConnection()
    {
        var directory = CreateDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var runtime = new FakeRuntime { OnSelect = version => { if (version == "new") cancellation.Cancel(); } };
            var journal = new UpdateJournal(directory);
            var coordinator = new UpdateCoordinator(journal, runtime);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, cancellation.Token));
            Assert.AreEqual(UpdatePhase.Switching, (await journal.ReadAsync(context.CancellationToken))!.Phase);
            var recovered = await coordinator.RecoverAsync(context.CancellationToken);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(UpdatePhase.Failed, recovered.Phase);
            Assert.AreEqual("new", runtime.Selected);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("stop", UpdatePhase.Stopping)]
    [DataRow("select:new", UpdatePhase.Switching)]
    [DataRow("start", UpdatePhase.Starting)]
    [DataRow("health", UpdatePhase.Verifying)]
    public async Task InterruptionAtEachServiceOperationPreservesState(string interruptedAction, UpdatePhase recordedPhase)
    {
        var directory = CreateDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var runtime = new FakeRuntime { OnAction = action => { if (action == interruptedAction) cancellation.Cancel(); } };
            var journal = new UpdateJournal(directory);
            var coordinator = new UpdateCoordinator(journal, runtime);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, cancellation.Token));
            Assert.AreEqual(recordedPhase, (await journal.ReadAsync(context.CancellationToken))!.Phase);

            // Model a restarted Updater: the new instance reads only the durable journal.
            runtime.OnAction = null;
            var recovered = await new UpdateCoordinator(new UpdateJournal(directory), runtime).RecoverAsync(context.CancellationToken);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(UpdatePhase.Failed, recovered.Phase);
            Assert.IsFalse(recovered.RetryAllowed);
            Assert.DoesNotContain("select:old", runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task RestartAfterSchemaCreationBeforeFirstUpdateCanContinue()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "update.db")};Pooling=False"))
            {
                await connection.OpenAsync(context.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE UpdateJournal (Id INTEGER PRIMARY KEY, State TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(context.CancellationToken);
            }
            var journal = new UpdateJournal(directory);
            Assert.IsNull(await journal.ReadAsync(context.CancellationToken));
            var result = await new UpdateCoordinator(journal, new FakeRuntime()).ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Succeeded, result.Phase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task CorruptJournalDoesNotTouchAgent()
    {
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "update.db"), "not a database", context.CancellationToken);
            var runtime = new FakeRuntime();
            var coordinator = new UpdateCoordinator(new UpdateJournal(directory), runtime);
            await Assert.ThrowsExactlyAsync<SqliteException>(() => coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, context.CancellationToken));
            Assert.IsEmpty(runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task JournalFailureBeforeStoppingDoesNotInterruptAgent()
    {
        var runtime = new FakeRuntime();
        var journal = new FailingJournal();
        await Assert.ThrowsExactlyAsync<IOException>(() => new UpdateCoordinator(journal, runtime)
            .ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, context.CancellationToken));
        Assert.IsEmpty(runtime.Actions);
        Assert.AreEqual(UpdatePhase.Prepared, journal.State!.Phase);
    }

    [TestMethod]
    public async Task FailedUpdateDoesNotRetryOrRollBackOnRestart()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime { Healthy = _ => false };
            var journal = new UpdateJournal(directory);
            var coordinator = new UpdateCoordinator(journal, runtime);
            var state = await coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Failed, state.Phase);
            var actionCount = runtime.Actions.Count;
            Assert.AreEqual(state.Phase, (await coordinator.RecoverAsync(context.CancellationToken))!.Phase);
            Assert.HasCount(actionCount, runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task LegacyRollbackFailureBecomesAdministratorActionWithoutChangingService()
    {
        var directory = CreateDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var journal = new UpdateJournal(directory);
            var id = Guid.CreateVersion7();
            await journal.WriteAsync(new UpdateState(id, "old", "new", UpdatePhase.RollbackFailed, now,
                [new(UpdatePhase.RollbackFailed, now, "Interrupted")], Error: "Interrupted"), context.CancellationToken);
            var runtime = new FakeRuntime();
            var recovered = await new UpdateCoordinator(journal, runtime).RecoverAsync(context.CancellationToken);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(UpdatePhase.Failed, recovered.Phase);
            Assert.AreEqual("Interrupted", recovered.Error);
            Assert.IsEmpty(runtime.Actions);
            var replacement = await new UpdateCoordinator(journal, runtime).ApplyAsync(Guid.CreateVersion7(), "old", "replacement",
                Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Succeeded, replacement.Phase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task LegacyRetryableRollbackDoesNotRestartSameAssignment()
    {
        var directory = CreateDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.CreateVersion7();
            var journal = new UpdateJournal(directory);
            await journal.WriteAsync(new UpdateState(id, "old", "new", UpdatePhase.RolledBack, now,
                [new(UpdatePhase.RolledBack, now, "Interrupted")], Error: "Interrupted", RetryAllowed: true), context.CancellationToken);
            var runtime = new FakeRuntime();
            var coordinator = new UpdateCoordinator(journal, runtime);
            var same = await coordinator.ApplyAsync(id, "old", "new", Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.RolledBack, same.Phase);
            Assert.IsEmpty(runtime.Actions);
            var replacement = await coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "replacement", Authorized, context.CancellationToken);
            Assert.AreEqual(UpdatePhase.Succeeded, replacement.Phase);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task StopTimeoutNeverSwitchesVersionOrRetriesOnRestart()
    {
        var directory = CreateDirectory();
        try
        {
            var runtime = new FakeRuntime { OnAction = action => { if (action == "stop") throw new TimeoutException(); } };
            var coordinator = new UpdateCoordinator(new UpdateJournal(directory), runtime);
            var state = await coordinator.ApplyAsync(Guid.CreateVersion7(), "old", "new", Authorized, context.CancellationToken);

            Assert.AreEqual(UpdatePhase.Failed, state.Phase);
            Assert.AreEqual(nameof(TimeoutException), state.Error);
            Assert.AreEqual("old", await runtime.GetSelectedDirectoryAsync(context.CancellationToken));
            Assert.DoesNotContain("select:new", runtime.Actions);

            state = (await coordinator.RecoverAsync(context.CancellationToken))!;
            Assert.AreEqual(UpdatePhase.Failed, state.Phase);
            Assert.AreEqual("old", await runtime.GetSelectedDirectoryAsync(context.CancellationToken));
            Assert.DoesNotContain("select:new", runtime.Actions);
            var actions = runtime.Actions.Count;
            Assert.AreEqual(UpdatePhase.Failed, (await coordinator.RecoverAsync(context.CancellationToken))!.Phase);
            Assert.HasCount(actions, runtime.Actions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private string CreateDirectory()
    {
        var path = Path.Combine(context.TestRunDirectory!, $"update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRuntime : IAgentUpdateRuntime
    {
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) => Task.FromResult(Selected ?? "old");
        public List<string> Actions { get; } = [];
        public string? Selected { get; private set; }
        public Func<string?, bool> Healthy { get; init; } = _ => true;
        public Action<string>? OnSelect { get; init; }
        public Action<string>? OnAction { get; set; }
        private void Record(string action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Actions.Add(action);
            OnAction?.Invoke(action);
            token.ThrowIfCancellationRequested();
        }
        public Task StopAsync(CancellationToken token) { Record("stop", token); return Task.CompletedTask; }
        public Task SelectVersionAsync(string versionDirectory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Selected = versionDirectory; Record($"select:{versionDirectory}", token);
            OnSelect?.Invoke(versionDirectory); token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken token) { Record("start", token); return Task.CompletedTask; }
        public Task<bool> WaitHealthyAsync(CancellationToken token) { Record("health", token); return Task.FromResult(Healthy(Selected)); }
    }

    private sealed class FailingJournal : IUpdateJournal
    {
        public UpdateState? State { get; private set; }
        public Task<UpdateState?> ReadAsync(CancellationToken token) => Task.FromResult(State);
        public Task WriteAsync(UpdateState state, CancellationToken token)
        {
            if (State is not null) throw new IOException("Simulated journal failure.");
            State = state;
            return Task.CompletedTask;
        }
    }
}

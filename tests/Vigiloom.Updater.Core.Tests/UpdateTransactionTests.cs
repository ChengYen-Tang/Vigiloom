using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Updates;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdateTransactionTests(TestContext context)
{
    [TestMethod]
    public async Task VerifiedPackageIsPreparedAndAppliedOnlyOnce()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var handler = new AssignmentHandler(target);
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(root);
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            var previous = Path.Combine(root, "old");
            var result = await ApplyAsync(transaction, verified, target, archive, root, previous);
            Assert.AreEqual(UpdatePhase.Succeeded, result.Phase);
            Assert.AreEqual("new", await File.ReadAllTextAsync(Path.Combine(result.TargetDirectory, "Vigiloom.Agent.exe"), context.CancellationToken));
            Assert.AreEqual(3, handler.Calls);
            var count = runtime.Actions.Count;
            var again = await ApplyAsync(transaction, verified, target, archive, root, previous);
            Assert.AreEqual(UpdatePhase.Succeeded, again.Phase);
            Assert.HasCount(count, runtime.Actions);
            Assert.HasCount(1, Directory.GetDirectories(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task MissingPreviousExecutableDoesNotInterruptAgent()
    {
        var root = CreateDirectory();
        try
        {
            File.Delete(Path.Combine(root, "old", "Vigiloom.Agent.exe"));
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var handler = new AssignmentHandler(target);
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(root);
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ApplyAsync(transaction, verified, target, archive, root, Path.Combine(root, "old")));
            Assert.IsEmpty(runtime.Actions);
            Assert.AreEqual(0, handler.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DifferentServiceVersionDoesNotInterruptAgent()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            using var http = new HttpClient(new AssignmentHandler(target));
            var runtime = new TestRuntime(Path.Combine(root, "different"));
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ApplyAsync(transaction, verified, target, archive, root, Path.Combine(root, "old")));
            Assert.IsEmpty(runtime.Actions);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AssignmentChangedDuringStopPreservesPreparedFiles()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var handler = new AssignmentHandler(target) { ChangedAt = 3 };
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(root);
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            var previous = Path.Combine(root, "old");
            var result = await ApplyAsync(transaction, verified, target, archive, root, previous);
            Assert.AreEqual(UpdatePhase.Failed, result.Phase);
            Assert.AreEqual(previous, await runtime.GetSelectedDirectoryAsync(context.CancellationToken));
            Assert.IsTrue(Directory.Exists(result.TargetDirectory));
            Assert.DoesNotContain("select:" + result.TargetDirectory, runtime.Actions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AssignmentChangedDuringPreparationRemovesUnreferencedStaging()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var handler = new AssignmentHandler(target) { ChangedAt = 2 };
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(root);
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ApplyAsync(transaction, verified, target, archive, root, Path.Combine(root, "old")));
            Assert.IsEmpty(runtime.Actions);
            Assert.IsEmpty(Directory.GetDirectories(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task AuthorizationUnavailableDuringPreparationRemovesUnreferencedStaging()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var handler = new AssignmentHandler(target) { UnavailableAt = 2 };
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(root);
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            await Assert.ThrowsExactlyAsync<UpdateAuthorizationUnavailableException>(() => ApplyAsync(transaction, verified, target, archive, root, Path.Combine(root, "old")));
            Assert.IsEmpty(runtime.Actions);
            Assert.IsEmpty(Directory.GetDirectories(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task TemporaryFailureAfterStopPreservesPreparedFiles()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var handler = new AssignmentHandler(target) { UnavailableAt = 3 };
            using var http = new HttpClient(handler);
            var runtime = new TestRuntime(root);
            var transaction = new UpdateTransaction(http, new UpdateJournal(Path.Combine(root, "state")), runtime, TimeProvider.System);
            var previous = Path.Combine(root, "old");
            var failed = await ApplyAsync(transaction, verified, target, archive, root, previous);
            Assert.AreEqual(UpdatePhase.Failed, failed.Phase);
            Assert.IsFalse(failed.RetryAllowed);
            Assert.IsTrue(Directory.Exists(failed.TargetDirectory));
            var retried = await ApplyAsync(transaction, verified, target, archive, root, previous);
            Assert.AreEqual(UpdatePhase.Failed, retried.Phase);
            Assert.AreEqual(failed.TargetDirectory, retried.TargetDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InterruptedUpdatePreservesStagingAndDoesNotRetry()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var previous = Path.Combine(root, "old");
            var abandoned = Path.Combine(root, "staging", Guid.CreateVersion7().ToString("N"));
            var unrelated = Path.Combine(root, "staging", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(abandoned);
            Directory.CreateDirectory(unrelated);
            await File.WriteAllTextAsync(Path.Combine(abandoned, "Vigiloom.Agent.exe"), "new", context.CancellationToken);
            var journal = new UpdateJournal(Path.Combine(root, "state"));
            var now = DateTimeOffset.UtcNow;
            await journal.WriteAsync(new UpdateState(target.UpdateId, previous, abandoned, UpdatePhase.Stopping, now,
                [new(UpdatePhase.Stopping, now, null)]), context.CancellationToken);
            var runtime = new TestRuntime(root);
            var recovered = await new UpdateCoordinator(new UpdateJournal(Path.Combine(root, "state")), runtime).RecoverAsync(context.CancellationToken);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(UpdatePhase.Failed, recovered.Phase);
            Assert.IsFalse(recovered.RetryAllowed);

            using var http = new HttpClient(new AssignmentHandler(target));
            var transaction = new UpdateTransaction(http, journal, runtime, TimeProvider.System);
            var result = await ApplyAsync(transaction, verified, target, archive, root, previous);
            Assert.AreEqual(UpdatePhase.Failed, result.Phase);
            Assert.IsTrue(Directory.Exists(abandoned));
            Assert.IsTrue(Directory.Exists(unrelated));
            Assert.IsTrue(File.Exists(Path.Combine(previous, "Vigiloom.Agent.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NewAssignmentAfterFailureSwitchesAndCleansFailedFilesOnlyAfterSuccess()
    {
        var root = CreateDirectory();
        try
        {
            using var archive = Archive();
            var (verified, target) = Manifest(archive);
            var failedDirectory = Path.Combine(root, "staging", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(failedDirectory);
            await File.WriteAllTextAsync(Path.Combine(failedDirectory, "Vigiloom.Agent.exe"), "failed", context.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var previous = Path.Combine(root, "old");
            var journal = new UpdateJournal(Path.Combine(root, "state"));
            await journal.WriteAsync(new UpdateState(Guid.CreateVersion7(), previous, failedDirectory, UpdatePhase.Failed,
                now, [new(UpdatePhase.Failed, now, "Interrupted")]), context.CancellationToken);
            var runtime = new TestRuntime(root);
            await runtime.SelectVersionAsync(failedDirectory, context.CancellationToken);
            using var http = new HttpClient(new AssignmentHandler(target));
            var transaction = new UpdateTransaction(http, journal, runtime, TimeProvider.System);
            var result = await ApplyAsync(transaction, verified, target, archive, root, failedDirectory);

            Assert.AreEqual(UpdatePhase.Succeeded, result.Phase);
            Assert.IsFalse(Directory.Exists(failedDirectory));
            Assert.IsTrue(File.Exists(Path.Combine(previous, "Vigiloom.Agent.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(result.TargetDirectory, "Vigiloom.Agent.exe")));
            Assert.AreEqual(result.TargetDirectory, runtime.Selected);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CleanupDoesNotDeletePreviousOrUnownedDirectories()
    {
        var root = CreateDirectory();
        try
        {
            var previous = Path.Combine(root, "old");
            var unrelated = Path.Combine(root, "not-staging", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(unrelated);
            var now = DateTimeOffset.UtcNow;
            var state = new UpdateState(Guid.CreateVersion7(), previous, unrelated, UpdatePhase.RolledBack, now, []);
            PreparedPackageCleanup.RemoveRolledBack(state, root);
            PreparedPackageCleanup.RemoveRolledBack(state with { TargetDirectory = previous }, root);
            Assert.IsTrue(Directory.Exists(unrelated));
            Assert.IsTrue(File.Exists(Path.Combine(previous, "Vigiloom.Agent.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CleanupKeepsCurrentAndPreviousAfterSuccessfulUpdate()
    {
        var root = CreateDirectory();
        try
        {
            var staging = Path.Combine(root, "staging");
            var old = Path.Combine(staging, Guid.CreateVersion7().ToString("N"));
            var previous = Path.Combine(staging, Guid.CreateVersion7().ToString("N"));
            var current = Path.Combine(staging, Guid.CreateVersion7().ToString("N"));
            var unowned = Path.Combine(staging, "unowned");
            foreach (var path in new[] { old, previous, current, unowned }) Directory.CreateDirectory(path);
            var now = DateTimeOffset.UtcNow;
            var state = new UpdateState(Guid.CreateVersion7(), previous, current, UpdatePhase.Succeeded, now, []);
            PreparedPackageCleanup.RemoveSuperseded(state with { Phase = UpdatePhase.RollingBack }, root, current);
            Assert.IsTrue(Directory.Exists(old));
            Assert.ThrowsExactly<InvalidOperationException>(() => PreparedPackageCleanup.RemoveSuperseded(state, root, previous));
            Assert.IsTrue(Directory.Exists(old));
            PreparedPackageCleanup.RemoveSuperseded(state, root, current);
            Assert.IsFalse(Directory.Exists(old));
            Assert.IsTrue(Directory.Exists(previous));
            Assert.IsTrue(Directory.Exists(current));
            Assert.IsTrue(Directory.Exists(unowned));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void CleanupDoesNotFollowLinkedDirectory()
    {
        var root = CreateDirectory();
        try
        {
            var staging = Path.Combine(root, "staging");
            var previous = Path.Combine(root, "old");
            var current = Path.Combine(staging, Guid.CreateVersion7().ToString("N"));
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(current);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep"), "data");
            var link = Path.Combine(staging, Guid.CreateVersion7().ToString("N"));
            Directory.CreateSymbolicLink(link, outside);
            var now = DateTimeOffset.UtcNow;
            var state = new UpdateState(Guid.CreateVersion7(), previous, current, UpdatePhase.Succeeded, now, []);
            PreparedPackageCleanup.RemoveSuperseded(state, root, current);
            Assert.IsTrue(File.Exists(Path.Combine(outside, "keep")));
            Assert.IsTrue(Directory.Exists(link));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private Task<UpdateState> ApplyAsync(UpdateTransaction transaction, VerifiedManifest verified, AgentVersionTarget target,
        Stream archive, string root, string previous) => transaction.ApplyAsync(new Uri("https://vigiloom.example/"), "vla_test", target,
            verified, archive, root, previous, "win-x64", "1.0.0", "1.0.0", context.CancellationToken);

    private string CreateDirectory()
    {
        var path = Path.Combine(context.TestRunDirectory!, $"transaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "old"));
        File.WriteAllText(Path.Combine(path, "old", "Vigiloom.Agent.exe"), "old");
        return path;
    }

    private static MemoryStream Archive()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent.exe").Open())) writer.Write("new");
        stream.Position = 0;
        return stream;
    }

    private static (VerifiedManifest, AgentVersionTarget) Manifest(MemoryStream archive)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive.ToArray()));
        var json = $$"""{"schema":1,"version":"1.0.0","rid":"win-x64","size":{{archive.Length}},"sha256":"{{hash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""";
        var verified = new VerifiedManifest(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow.AddMinutes(5));
        var parsed = PackageManifest.Parse(verified, DateTimeOffset.UtcNow);
        return (verified, new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), parsed.Version, parsed.RuntimeIdentifier,
            parsed.ManifestSha256, Convert.ToBase64String(new byte[8])));
    }

    private sealed class AssignmentHandler(AgentVersionTarget target) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int ChangedAt { get; init; }
        public int UnavailableAt { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ++Calls;
            if (Calls == ChangedAt) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (Calls == UnavailableAt) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) });
        }
    }

    private sealed class TestRuntime(string root) : IAgentUpdateRuntime
    {
        public List<string> Actions { get; } = [];
        public string? Selected { get; private set; }
        public Task<string> GetSelectedDirectoryAsync(CancellationToken token) => Task.FromResult(Selected ?? Path.Combine(root, "old"));
        public Task StopAsync(CancellationToken token) { Actions.Add("stop"); return Task.CompletedTask; }
        public Task SelectVersionAsync(string directory, CancellationToken token) { Selected = directory; Actions.Add("select:" + directory); return Task.CompletedTask; }
        public Task StartAsync(CancellationToken token) { Actions.Add("start"); return Task.CompletedTask; }
        public Task<bool> WaitHealthyAsync(CancellationToken token) { Actions.Add("health"); return Task.FromResult(true); }
    }
}

using System.Net;
using System.Text;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class InventoryCycleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Run_WindowsRelationshipsRequireCapabilityAtEnqueue(bool initiallySupported, bool removeDuringCollection)
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var handler = new StatusHandler { Schemas = initiallySupported ? "4, 5, 7, 8, 9, 10" : "4, 5, 7, 8, 9", SupportsProcesses = true, SupportsUpdates = true };
            using var http = new HttpClient(handler);
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            var updateId = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var item = new InventoryEntry($"{updateId:D}:1", "update", UpdateId: updateId, UpdateRevision: 1, UpdateInstalled: true,
                UpdateRebootRequired: false, UpdateCves: [], UpdateKbs: [], UpdateServerSelection: 0, UpdateServiceId: Guid.Empty,
                UpdateSupersededIds: ["22222222-2222-4222-8222-222222222222"]);
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource _, CancellationToken __)
            {
                if (removeDuringCollection) handler.Schemas = "4, 5, 7, 8, 9";
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Partial, [item]));
            }
            Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.WindowsUpdate, Collect, token)).Kind);
            var batch = await new DurableBatchQueue(vault, id).PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out var chunk));
            var supported = initiallySupported && !removeDuringCollection;
            Assert.AreEqual(supported ? InventoryChunkCodec.WindowsUpdateRelationshipsSchemaVersion : InventoryChunkCodec.WindowsUpdateSchemaVersion, chunk!.SchemaVersion);
            Assert.AreEqual(supported ? 0 : null, chunk.Items.Single().UpdateServerSelection);
            Assert.AreEqual(supported ? item.UpdateSupersededIds![0] : null, chunk.Items.Single().UpdateSupersededIds?.SingleOrDefault());
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, true, true, false)]
    [DataRow(true, false, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, true, true, false)]
    [DataRow(true, true, false, true)]
    [DataRow(true, true, true, true)]
    public async Task Run_NewInventoryFieldsRequireCurrentServerSchema(bool windows, bool initiallySupported, bool removeDuringCollection, bool hasUpdateContract)
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var handler = new StatusHandler { Schemas = initiallySupported ? "4, 5, 7, 8" : null, SupportsProcesses = true, SupportsUpdates = hasUpdateContract };
            using var http = new HttpClient(handler);
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            var source = windows ? InventorySource.HostSystem : InventorySource.LinuxDpkg;
            var entry = windows
                ? new InventoryEntry("host", "machine", "10", "X64", OperatingSystem: "Windows", ProcessArchitecture: "X64",
                    Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows", WindowsEdition: "ServerStandard", WindowsBuild: 20348, WindowsRevision: 1, WindowsInstallationType: "Server Core")
                : new InventoryEntry("pkg", "pkg", "1", "amd64", SourcePackageName: "source", SourcePackageVersion: "1");
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource _, CancellationToken __)
            {
                if (removeDuringCollection) { handler.Schemas = null; handler.SupportsUpdates = false; }
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Complete, [entry]));
            }
            Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, source, Collect, token)).Kind);
            var batch = await new DurableBatchQueue(vault, id).PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out var chunk));
            var supported = initiallySupported && !removeDuringCollection;
            Assert.AreEqual(supported ? (windows ? (hasUpdateContract ? InventoryChunkCodec.WindowsUpdateSchemaVersion : InventoryChunkCodec.WindowsReleaseSchemaVersion) : InventoryChunkCodec.DpkgSourceSchemaVersion) : InventoryChunkCodec.SchemaVersion, chunk!.SchemaVersion);
            Assert.AreEqual(supported ? (windows ? "ServerStandard" : "source") : null,
                windows ? chunk.Items.Single().WindowsEdition : chunk.Items.Single().SourcePackageName);
            if (windows) Assert.AreEqual(supported && hasUpdateContract ? "Server Core" : null, chunk.Items.Single().WindowsInstallationType);
            Assert.AreEqual(InventoryOutcome.Complete, chunk.Outcome);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Run_WindowsUpdateRequiresCapabilityBeforeAndAfterCollection()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var handler = new StatusHandler { SupportsProcesses = true };
            using var http = new HttpClient(handler);
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            var calls = 0;
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource _, CancellationToken __)
            {
                calls++;
                handler.SupportsUpdates = false;
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Partial, []));
            }
            Assert.AreEqual(InventoryCycleKind.ServerUnsupported, (await cycle.RunAsync(vault, server, InventorySource.WindowsUpdate, Collect, token)).Kind);
            Assert.AreEqual(0, calls);
            handler.SupportsUpdates = true;
            Assert.AreEqual(InventoryCycleKind.ServerUnsupported, (await cycle.RunAsync(vault, server, InventorySource.WindowsUpdate, Collect, token)).Kind);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(0L, (await new DurableBatchQueue(vault, id).GetStatusAsync(token)).TotalBatches);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Run_SnapshotLargerThanQueueReportsFailureInsteadOfWaitingForever()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var capacity = new AgentQueueCapacity(AgentQueueCapacity.MinimumBytes, 1);
            await new AgentQueueCapacityStore(vault, server, id).SaveAsync(AgentQueueCapacitySnapshot.Create(capacity), null, DateTimeOffset.UtcNow, token);
            using var http = new HttpClient(new StatusHandler());
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource source, CancellationToken _)
            {
                var items = Enumerable.Range(0, 101).Select(i => new InventoryEntry($"p{i}", $"p{i}", "1", "x64")).ToArray();
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Complete, items));
            }
            var result = await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token);
            Assert.AreEqual(InventoryCycleKind.QueuedFailure, result.Kind);
            var batch = await new DurableBatchQueue(vault, id).PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out var chunk));
            Assert.AreEqual(InventoryOutcome.Error, chunk!.Outcome);
            Assert.IsEmpty(chunk.Items);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Run_QueuesOnceWaitsAndReconcilesAfterCentralCommit()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var handler = new StatusHandler();
            using var http = new HttpClient(handler);
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            var calls = 0;
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource source, CancellationToken _)
            {
                calls++;
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Complete,
                    [new("package", "package", "1", "x64")]));
            }
            var result = await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token);
            Assert.AreEqual(InventoryCycleKind.Queued, result.Kind);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(InventoryCycleKind.Waiting, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(1, calls);
            var owner = new AgentSettingsOwner(id, server.AbsoluteUri, vault.StateDirectory);
            var outstanding = await new DurableBatchQueue(vault, id).ReadOutstandingInventoryAsync(owner, token);
            Assert.IsNotNull(outstanding);
            handler.Body = $"[{{\"source\":3,\"highWaterGeneration\":1,\"lastAttemptGeneration\":1,\"lastOutcome\":1,\"lastStatus\":2,\"lastSnapshotId\":\"{outstanding.SnapshotId:D}\"}}]";
            Assert.AreEqual(InventoryCycleKind.Complete, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(0L, (await new DurableBatchQueue(vault, id).GetStatusAsync(token)).TotalBatches);
            Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(2, calls);
            // A delayed request from a restored/older local queue can commit the same generation
            // with a different snapshot ID. It must not acknowledge the new local snapshot.
            var otherSnapshot = Guid.CreateVersion7();
            handler.Body = $"[{{\"source\":3,\"highWaterGeneration\":2,\"lastAttemptGeneration\":2,\"lastOutcome\":1,\"lastStatus\":2,\"lastSnapshotId\":\"{otherSnapshot:D}\"}}]";
            Assert.AreEqual(InventoryCycleKind.Rejected, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(1L, (await new DurableBatchQueue(vault, id).GetStatusAsync(token)).QuarantinedBatches);
            Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            var third = await new DurableBatchQueue(vault, id).ReadOutstandingInventoryAsync(owner, token);
            Assert.AreEqual(3L, third!.Generation);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";
        public bool SupportsProcesses { get; set; }
        public bool SupportsUpdates { get; set; }
        public string? Schemas { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
            if (SupportsProcesses) response.Headers.Add("Vigiloom-Inventory-Contract", "5");
            if (SupportsUpdates) response.Headers.Add("Vigiloom-Windows-Update-Contract", "9");
            if (Schemas is not null) response.Headers.Add("Vigiloom-Inventory-Schemas", Schemas);
            return Task.FromResult(response);
        }
    }
}

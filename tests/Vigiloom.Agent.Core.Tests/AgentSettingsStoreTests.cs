using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentSettingsStoreTests(TestContext testContext)
{
    [TestMethod]
    public async Task SettingsOwnership_ExcludesAnotherSynchronizerWithoutBlockingIdentityOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests", Path.GetRandomFileName());
        try
        {
            var vault = new IdentityVault(root);
            await using (vault.AcquireSettingsOwnership())
            {
                IAsyncDisposable? contender = null;
                try { Assert.Throws<IOException>(() => contender = new IdentityVault(root).AcquireSettingsOwnership()); }
                finally { if (contender is not null) await contender.DisposeAsync(); }
                await using var identity = vault.Acquire();
                Assert.IsNull(await identity.ReadAsync(testContext.CancellationToken));
            }
            await using var next = new IdentityVault(root).AcquireSettingsOwnership();
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task SavedSettings_SurviveRestartAndInvalidUpdatesDoNotReplaceThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = "unchanged" }, token);
            var server = new Uri("https://example.test/");
            var store = new AgentSettingsStore(vault, server, id);
            Assert.IsNull(await store.ReadAsync(token));
            var snapshot = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var time = new DateTimeOffset(2026, 11, 2, 12, 30, 0, TimeSpan.FromHours(8));
            var saved = await store.SaveAsync(snapshot, null, time, token);
            Assert.AreEqual(time.ToUniversalTime(), saved.AppliedAt);
            var restarted = new AgentSettingsStore(new IdentityVault(root), server, id);
            Assert.AreEqual(saved, await restarted.ReadAsync(token));
            // An older Agent knows only the identity file and may rewrite it during rotation.
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = "unchanged" }, token);
            Assert.AreEqual(saved, await restarted.ReadAsync(token));
            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.SaveAsync(snapshot with { Revision = "invalid" }, saved.SaveToken, time, token));
            Assert.AreEqual(saved, await restarted.ReadAsync(token));
            await using var verify = vault.Acquire();
            Assert.AreEqual("unchanged", (await verify.ReadAsync(token))!.MachineSecret);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Settings_ConcurrentWritersCannotReplaceANewerSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = "unchanged" }, token);
            var server = new Uri("https://example.test/");
            var firstStore = new AgentSettingsStore(vault, server, id);
            var secondStore = new AgentSettingsStore(new IdentityVault(root), server, id);
            var first = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60 });
            var second = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 90 });
            async Task<bool> Save(AgentSettingsStore store, AgentSettingsSnapshot value)
            {
                try { await store.SaveAsync(value, null, DateTimeOffset.UtcNow, token); return true; }
                catch (AgentSettingsConflictException) { return false; }
            }
            var results = await Task.WhenAll(Save(firstStore, first), Save(secondStore, second));
            Assert.AreEqual(1, results.Count(x => x));
            Assert.AreEqual(results[0] ? first : second, (await firstStore.ReadAsync(token))!.Snapshot);
            await Assert.ThrowsAsync<AgentSettingsConflictException>(() => secondStore.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Settings_PreviousIdentityStateIsNotAppliedToNewIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var oldId = Guid.CreateVersion7();
            var newId = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var server = new Uri("https://example.test/");
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = oldId, MachineSecret = "old" }, token);
            var previous = new AgentSettingsStore(vault, server, oldId);
            await previous.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token);
            var path = Path.Combine(root, "settings.dat");
            var originalBytes = await File.ReadAllBytesAsync(path, token);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = newId, MachineSecret = "new" }, token);
            var current = new AgentSettingsStore(vault, server, newId);
            await Assert.ThrowsAsync<InvalidDataException>(() => current.ReadAsync(token));
            await Assert.ThrowsAsync<InvalidDataException>(() => current.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => previous.ReadAsync(token));
            var newServer = new Uri("https://elsewhere.test/");
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = newServer.AbsoluteUri, AgentId = oldId, MachineSecret = "new" }, token);
            var moved = new AgentSettingsStore(vault, newServer, oldId);
            await Assert.ThrowsAsync<InvalidDataException>(() => moved.ReadAsync(token));
            await Assert.ThrowsAsync<InvalidDataException>(() => moved.SaveAsync(AgentSettingsSnapshot.Create(AgentSettings.Default), null, DateTimeOffset.UtcNow, token));
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Settings_RevertingValuesDoesNotPermitAnOlderResponseToOverwriteThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = "unchanged" }, token);
            var store = new AgentSettingsStore(vault, new Uri("https://example.test/"), id);
            var a = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var b = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60 });
            var firstA = await store.SaveAsync(a, null, DateTimeOffset.UtcNow, token);
            var currentB = await store.SaveAsync(b, firstA.SaveToken, DateTimeOffset.UtcNow, token);
            var secondA = await store.SaveAsync(a, currentB.SaveToken, DateTimeOffset.UtcNow, token);
            Assert.AreEqual(firstA.Snapshot.Revision, secondA.Snapshot.Revision);
            Assert.AreNotEqual(firstA.SaveToken, secondA.SaveToken);
            await Assert.ThrowsAsync<AgentSettingsConflictException>(() => store.SaveAsync(b, firstA.SaveToken, DateTimeOffset.UtcNow, token));
            Assert.AreEqual(secondA, await store.ReadAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Settings_AreBoundToTheirIdentityAndUnknownFormatsAreNotOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var snapshot = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var unknown = new AgentAppliedSettings(snapshot with { SchemaVersion = 2 }, DateTimeOffset.UtcNow, Guid.CreateVersion7(), id, "https://example.test/");
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = "unchanged" }, token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(unknown);
            var encoded = OperatingSystem.IsWindows() ? ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
            var path = Path.Combine(root, "settings.dat");
            await File.WriteAllBytesAsync(path, encoded, token);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var validOwner = new AgentSettingsStore(vault, new Uri("https://example.test/"), id);
            await Assert.ThrowsAsync<InvalidDataException>(() => validOwner.ReadAsync(token));
            await Assert.ThrowsAsync<InvalidDataException>(() => validOwner.SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token));
            var wrongServer = new AgentSettingsStore(vault, new Uri("https://elsewhere.test/"), id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongServer.ReadAsync(token));
            var wrongAgent = new AgentSettingsStore(vault, new Uri("https://example.test/"), Guid.CreateVersion7());
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongAgent.SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token));
            CollectionAssert.AreEqual(encoded, await File.ReadAllBytesAsync(path, token));
        }
        finally { Cleanup(root); }
    }

    private static void Cleanup(string root)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomSettingsStoreTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

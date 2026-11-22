using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vigiloom.Updater.Hosting;
using Vigiloom.Updater.Core.Updates;

namespace Vigiloom.Updater.Tests;

[TestClass]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task HostWithoutProductConfigurationStops()
    {
        var builder = UpdaterHost.CreateBuilder([]);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        await host.StartAsync(testContext.CancellationToken);
        await stopping.Task.WaitAsync(testContext.CancellationToken);

        await host.StopAsync(testContext.CancellationToken);
        Assert.IsTrue(lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task HostPreservesPreparedUpdateOnStartupWithoutContactingServer()
    {
        var root = Path.Combine(testContext.TestRunDirectory!, $"recovery-{Guid.NewGuid():N}");
        var stateDirectory = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        try
        {
            var journal = new UpdateJournal(stateDirectory);
            var now = DateTimeOffset.UtcNow;
            var prepared = Path.Combine(root, "staging", Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(prepared);
            await journal.WriteAsync(new UpdateState(Guid.CreateVersion7(), Path.Combine(root, "old"), prepared,
                UpdatePhase.Prepared, now, [new(UpdatePhase.Prepared, now, null)]), testContext.CancellationToken);
            var builder = UpdaterHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Updater:InstallationRoot"] = root,
                ["Vigiloom:Updater:StateDirectory"] = stateDirectory
            });
            using var host = builder.Build();
            await host.StartAsync(testContext.CancellationToken);
            UpdateState? state;
            do
            {
                state = await journal.ReadAsync(testContext.CancellationToken);
                if (state?.Phase != UpdatePhase.Failed) await Task.Delay(50, testContext.CancellationToken);
            } while (state?.Phase != UpdatePhase.Failed);
            Assert.IsTrue(Directory.Exists(prepared));
            Assert.AreEqual("Interrupted", state.Error);
            await host.StopAsync(testContext.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task HostDoesNotRecoverWhileAnotherUpdaterOwnsTheInstallation()
    {
        var root = Path.Combine(testContext.TestRunDirectory!, $"ownership-{Guid.NewGuid():N}");
        var stateDirectory = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        try
        {
            var journal = new UpdateJournal(stateDirectory);
            var now = DateTimeOffset.UtcNow;
            var prepared = Path.Combine(root, "staging", Guid.CreateVersion7().ToString("N"));
            await journal.WriteAsync(new UpdateState(Guid.CreateVersion7(), Path.Combine(root, "old"), prepared,
                UpdatePhase.Prepared, now, [new(UpdatePhase.Prepared, now, null)]), testContext.CancellationToken);
            await using var owner = new FileStream(Path.Combine(root, "updater.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = UpdaterHost.CreateBuilder([]);
            builder.Logging.AddProvider(new OwnershipLogProvider(signal));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Updater:InstallationRoot"] = root,
                ["Vigiloom:Updater:StateDirectory"] = stateDirectory
            });
            using var host = builder.Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            await host.StartAsync(testContext.CancellationToken);
            await signal.Task.WaitAsync(testContext.CancellationToken);
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
            await stopping.Task.WaitAsync(testContext.CancellationToken);
            Assert.AreEqual(UpdatePhase.Prepared, (await journal.ReadAsync(testContext.CancellationToken))?.Phase);
            await host.StopAsync(testContext.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task HostStopsWhenInstalledConfigurationCannotAcceptHandoffs()
    {
        var root = Path.Combine(testContext.TestRunDirectory!, $"incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var builder = UpdaterHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Updater:InstallationRoot"] = root,
                ["Vigiloom:Updater:StateDirectory"] = Path.Combine(root, "state")
            });
            using var host = builder.Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
            await host.StartAsync(testContext.CancellationToken);
            await stopping.Task.WaitAsync(testContext.CancellationToken);
            await host.StopAsync(testContext.CancellationToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task HostStopsWhenInstalledStatePathIsInvalid()
    {
        var builder = UpdaterHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vigiloom:Updater:InstallationRoot"] = Path.Combine(testContext.TestRunDirectory!, "installation"),
            ["Vigiloom:Updater:StateDirectory"] = "relative-state"
        });
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        await host.StartAsync(testContext.CancellationToken);
        await stopping.Task.WaitAsync(testContext.CancellationToken);
        await host.StopAsync(testContext.CancellationToken);
    }

    private sealed class OwnershipLogProvider(TaskCompletionSource signal) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new OwnershipLogger(signal);
        public void Dispose() { }
    }

    private sealed class OwnershipLogger(TaskCompletionSource signal) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Updater cannot acquire installation ownership", StringComparison.Ordinal))
                signal.TrySetResult();
        }
    }
}

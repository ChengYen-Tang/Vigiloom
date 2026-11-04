using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Telemetry;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class ReceptionRejectionBufferTests
{
    [TestMethod]
    public async Task FailedFlush_DoesNotRetryAnUncertainDelta()
    {
        var factory = new FailingFactory();
        var logger = new CaptureLogger();
        var diagnostics = new ReceptionRejectionDiagnostics(factory, logger);
        Assert.Throws<ArgumentException>(() => diagnostics.Record(ReceptionRejectionKind.CredentialRejected, ReceptionIngress.Otlp, Guid.CreateVersion7()));
        diagnostics.Record(ReceptionRejectionKind.CredentialRejected, ReceptionIngress.Otlp);
        await diagnostics.FlushAsync(CancellationToken.None);
        await diagnostics.FlushAsync(CancellationToken.None);
        Assert.AreEqual(1, factory.Attempts);
        Assert.HasCount(1, logger.Messages);
        Assert.DoesNotContain("secret-must-not-be-logged", logger.Messages[0]);
        Assert.Contains("InvalidOperationException", logger.Messages[0]);
    }

    private sealed class FailingFactory : IDbContextFactory<ServiceObservabilityDbContext>
    {
        public int Attempts { get; private set; }
        public ServiceObservabilityDbContext CreateDbContext()
        {
            Attempts++;
            throw new InvalidOperationException("secret-must-not-be-logged");
        }
    }

    private sealed class CaptureLogger : ILogger<ReceptionRejectionDiagnostics>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}

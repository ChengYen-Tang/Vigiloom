using Vigiloom.Contracts.Operations;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Delivers one authorized OTLP Log request to the central Log owner.</summary>
public interface IApplicationLogDelivery
{
    Task<LogReceipt> SendAsync(ApplicationLogBatch batch, CancellationToken token);
}

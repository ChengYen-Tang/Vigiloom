using System.Data.Common;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Observability;

internal sealed class HealthTransitionDispatcher(IServiceScopeFactory scopes) : IHealthTransitionSink
{
    public async Task RecordAsync(HealthTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TemporalHealthTransitionSink>()
            .RecordAsync(transition, connection, transaction, token);
    }
}

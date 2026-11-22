using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

/// <summary>Stages event identities, never their original contents, with the authenticated receipt.</summary>
internal sealed class HostLogDispatchWriter(OutboxWriter outbox) : IHostLogDispatchWriter
{
    public async Task StageAsync(DbConnection connection, DbTransaction transaction, HostLogDispatch dispatch, CancellationToken token)
    {
        if (connection is not SqlConnection sql || transaction is not SqlTransaction sqlTransaction)
            throw new ArgumentException("A SQL Server receipt transaction is required.");
        // Hold the read through receipt commit so an enable operation cannot cross this decision.
        await using var command = sql.CreateCommand();
        command.Transaction = sqlTransaction;
        command.CommandText = "SELECT TOP (1) 1 FROM [security].[SigmaRuleActivations] WITH (HOLDLOCK) WHERE [Enabled] = 1";
        var sigmaEnabled = await command.ExecuteScalarAsync(token) is not null;
        command.CommandText = "SELECT TOP (1) 1 FROM [security].[IntelligenceSources] WITH (HOLDLOCK) WHERE [Key] = 'signature-base' AND [Enabled] = 1 AND [IocDetectionEnabled] = 1";
        var iocEnabled = await command.ExecuteScalarAsync(token) is not null;
        if (!sigmaEnabled && !iocEnabled) return;
        var payload = JsonSerializer.Serialize(dispatch);
        if (sigmaEnabled)
            await outbox.WriteAsync(new(Guid.CreateVersion7(), HostLogDispatch.MessageType, payload), sql, sqlTransaction, token);
        if (iocEnabled)
            await outbox.WriteAsync(new(Guid.CreateVersion7(), HostLogDispatch.IocMessageType, payload), sql, sqlTransaction, token);
    }
}

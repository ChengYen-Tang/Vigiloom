using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Session-owned lock prevents duplicate downloads across refresh entry points.</summary>
internal sealed class IntelligenceRefreshLease(SecurityAnalysisDbContext context, string resource, int sessionId) : IAsyncDisposable
{
    private bool leaseLost;
    internal int SessionId => sessionId;
    internal bool IsFor(Guid sourceId) => string.Equals(resource, $"Vigiloom.Intelligence.Refresh.{sourceId:D}", StringComparison.Ordinal);
    internal async Task<bool> VerifyHeldAsync(CancellationToken token)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) { leaseLost = true; return false; }
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN @@SPID = @session THEN APPLOCK_MODE('public', @name, 'Session') ELSE 'NoLock' END";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.DbType = DbType.String;
            parameter.Size = 255;
            parameter.Value = resource;
            command.Parameters.Add(parameter);
            var sessionParameter = command.CreateParameter();
            sessionParameter.ParameterName = "@session";
            sessionParameter.DbType = DbType.Int32;
            sessionParameter.Value = sessionId;
            command.Parameters.Add(sessionParameter);
            var held = string.Equals(await command.ExecuteScalarAsync(token) as string, "Exclusive", StringComparison.Ordinal);
            if (!held) leaseLost = true;
            return held;
        }
        catch (DbException) { leaseLost = true; token.ThrowIfCancellationRequested(); return false; }
        catch (InvalidOperationException) { leaseLost = true; token.ThrowIfCancellationRequested(); return false; }
    }

    internal static async Task<IntelligenceRefreshLease?> TryAcquireAsync(IDbContextFactory<SecurityAnalysisDbContext> contexts, Guid sourceId, CancellationToken token)
    {
        var context = await contexts.CreateDbContextAsync(token);
        var resource = $"Vigiloom.Intelligence.Refresh.{sourceId:D}";
        try
        {
            await context.Database.OpenConnectionAsync(token);
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @result;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.DbType = DbType.String;
            parameter.Size = 255;
            parameter.Value = resource;
            command.Parameters.Add(parameter);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            if (result >= 0)
            {
                command.CommandText = "SELECT @@SPID";
                return new(context, resource, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
            }
            if (result == -1)
            {
                await context.DisposeAsync();
                return null;
            }
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Intelligence refresh lock failed with status {result}.");
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (context.Database.GetDbConnection().State == ConnectionState.Open)
                await context.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_releaseapplock @Resource = {resource}, @LockOwner = 'Session';");
        }
        // A lost session no longer holds its lock; SqlClient can briefly still report it as open.
        catch (DbException) when (leaseLost) { }
        catch (InvalidOperationException) when (leaseLost) { }
        finally { await context.DisposeAsync(); }
    }
}

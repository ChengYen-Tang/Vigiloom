using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Session-owned lock prevents duplicate downloads across refresh entry points.</summary>
internal sealed class IntelligenceRefreshLease(SecurityAnalysisDbContext context, string resource) : IAsyncDisposable
{
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
            if (result >= 0) return new(context, resource);
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
        finally { await context.DisposeAsync(); }
    }
}

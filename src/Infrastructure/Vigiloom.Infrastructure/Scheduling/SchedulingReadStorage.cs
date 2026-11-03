using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Vigiloom.Infrastructure.Scheduling;

/// <summary>Uses bounded SQL timeouts for synchronous Hangfire management reads only.</summary>
public sealed class SchedulingReadStorage
{
    public SchedulingReadStorage(IConfiguration configuration)
    {
        var connection = new SqlConnectionStringBuilder(SchedulingRegistration.Connection(configuration)) { ConnectTimeout = 5 };
        var options = SchedulingRegistration.StorageOptions(prepareSchema: false);
        options.CommandTimeout = TimeSpan.FromSeconds(5);
        options.TryAutoDetectSchemaDependentOptions = false;
        Storage = new SqlServerStorage(connection.ConnectionString, options);
    }

    public SqlServerStorage Storage { get; }
}

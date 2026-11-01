using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Infrastructure.Scheduling.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SchedulingDbContext>
{
    public SchedulingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "scheduling"))
            .Options;
        return new SchedulingDbContext(options);
    }
}

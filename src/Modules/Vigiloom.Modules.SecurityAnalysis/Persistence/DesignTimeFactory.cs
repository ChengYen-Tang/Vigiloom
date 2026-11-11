using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SecurityAnalysisDbContext>
{
    public SecurityAnalysisDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "security"))
            .Options;
        return new SecurityAnalysisDbContext(options);
    }
}

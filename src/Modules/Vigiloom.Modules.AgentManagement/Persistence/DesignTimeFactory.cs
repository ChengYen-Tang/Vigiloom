using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.AgentManagement.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AgentManagementDbContext>
{
    public AgentManagementDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<AgentManagementDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "agent"))
            .Options;
        return new AgentManagementDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<IdentityAccessDbContext>
{
    public IdentityAccessDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<IdentityAccessDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        return new IdentityAccessDbContext(options);
    }
}

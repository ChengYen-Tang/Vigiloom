using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

internal sealed class CentralLogsDesignTimeFactory : IDesignTimeDbContextFactory<CentralLogsDbContext>
{
    public CentralLogsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CentralLogsDbContext>()
        .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Operations_Development;Integrated Security=true;Encrypt=false",
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory_CentralLogs", "logs")).Options);
}

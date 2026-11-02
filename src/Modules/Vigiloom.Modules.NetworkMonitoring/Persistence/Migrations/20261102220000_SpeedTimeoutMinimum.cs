using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations;

public partial class SpeedTimeoutMinimum : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep existing schedules and queued runs compatible with the new minimum.
        // Completed and currently running measurements retain their original limits.
        migrationBuilder.Sql("""
            UPDATE network.SpeedSettings SET TimeoutSeconds = 40 WHERE TimeoutSeconds < 40;
            UPDATE network.SpeedRuns SET TimeoutSeconds = 40 WHERE State = 0 AND TimeoutSeconds < 40;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Previous values are unknown and cannot be restored safely.
    }
}

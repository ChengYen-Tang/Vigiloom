using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkerDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerDiagnosticSlots",
                schema: "operations",
                columns: table => new
                {
                    Slot = table.Column<int>(type: "int", nullable: false),
                    BootId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PingSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PingFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PingFailures = table.Column<int>(type: "int", nullable: false),
                    PingFailureKind = table.Column<short>(type: "smallint", nullable: false),
                    SpeedSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SpeedFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SpeedFailures = table.Column<int>(type: "int", nullable: false),
                    SpeedFailureKind = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerDiagnosticSlots", x => x.Slot)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_WorkerDiagnosticSlots_Failures", "[PingFailures] >= 0 AND [SpeedFailures] >= 0");
                    table.CheckConstraint("CK_WorkerDiagnosticSlots_Slot", "[Slot] BETWEEN 1 AND 64");
                });

            // Initialize the fixed pool once. These rows are mutable operational state,
            // not EF model-managed seed data that later migrations may reset or delete.
            migrationBuilder.Sql("""
                WITH slots AS (SELECT 1 AS Slot UNION ALL SELECT Slot + 1 FROM slots WHERE Slot < 64)
                INSERT INTO operations.WorkerDiagnosticSlots (Slot, PingFailures, PingFailureKind, SpeedFailures, SpeedFailureKind)
                SELECT Slot, 0, 0, 0, 0 FROM slots OPTION (MAXRECURSION 64);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerDiagnosticSlots",
                schema: "operations");
        }
    }
}

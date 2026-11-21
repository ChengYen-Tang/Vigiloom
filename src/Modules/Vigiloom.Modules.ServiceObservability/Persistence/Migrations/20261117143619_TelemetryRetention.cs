using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelemetryRetentionSettings",
                schema: "service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    LogDays = table.Column<int>(type: "int", nullable: false),
                    TraceDays = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelemetryRetentionSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_TelemetryRetentionSettings_Days", "[LogDays] BETWEEN 1 AND 3650 AND [TraceDays] BETWEEN 1 AND 3650");
                    table.CheckConstraint("CK_TelemetryRetentionSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_TelemetryRetentionSettings_Slot", "[Slot] = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraceSpans_Retention",
                schema: "service",
                table: "TraceSpans",
                columns: new[] { "StartedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_Retention",
                schema: "service",
                table: "LogRecords",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRetentionSettings_RowId",
                schema: "service",
                table: "TelemetryRetentionSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_TelemetryRetentionSettings_Slot",
                schema: "service",
                table: "TelemetryRetentionSettings",
                column: "Slot",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelemetryRetentionSettings",
                schema: "service");

            migrationBuilder.DropIndex(
                name: "IX_TraceSpans_Retention",
                schema: "service",
                table: "TraceSpans");

            migrationBuilder.DropIndex(
                name: "IX_LogRecords_Retention",
                schema: "service",
                table: "LogRecords");
        }
    }
}

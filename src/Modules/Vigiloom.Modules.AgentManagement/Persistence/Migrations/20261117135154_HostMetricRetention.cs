using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostMetricRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostMetricRetentionSettings",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    RawDays = table.Column<int>(type: "int", nullable: false),
                    RollupDays = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetricRetentionSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_HostMetricRetentionSettings_Days", "[RawDays] BETWEEN 1 AND 3650 AND [RollupDays] BETWEEN [RawDays] AND 3650");
                    table.CheckConstraint("CK_HostMetricRetentionSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_HostMetricRetentionSettings_Slot", "[Slot] = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_Retention",
                schema: "agent",
                table: "HostMetrics",
                columns: new[] { "OccurredAt", "RowId" },
                filter: "[Aggregated] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRollups_Retention",
                schema: "agent",
                table: "HostMetricRollups",
                columns: new[] { "WindowStart", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRetentionSettings_RowId",
                schema: "agent",
                table: "HostMetricRetentionSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRetentionSettings_Slot",
                schema: "agent",
                table: "HostMetricRetentionSettings",
                column: "Slot",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostMetricRetentionSettings",
                schema: "agent");

            migrationBuilder.DropIndex(
                name: "IX_HostMetrics_Retention",
                schema: "agent",
                table: "HostMetrics");

            migrationBuilder.DropIndex(
                name: "IX_HostMetricRollups_Retention",
                schema: "agent",
                table: "HostMetricRollups");
        }
    }
}

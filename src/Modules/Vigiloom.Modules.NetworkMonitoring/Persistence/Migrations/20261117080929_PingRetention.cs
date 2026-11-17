using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PingRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RawRetentionDays",
                schema: "network",
                table: "PingSettings",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "RollupRetentionDays",
                schema: "network",
                table: "PingSettings",
                type: "int",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PingSettings_Retention",
                schema: "network",
                table: "PingSettings",
                sql: "[RawRetentionDays] BETWEEN 1 AND 3650 AND [RollupRetentionDays] BETWEEN 1 AND 3650");

            migrationBuilder.CreateIndex(
                name: "IX_PingSamples_OccurredAt_RowId",
                schema: "network",
                table: "PingSamples",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_PingRollups_WindowStart_RowId",
                schema: "network",
                table: "PingRollups",
                columns: new[] { "WindowStart", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PingSettings_Retention",
                schema: "network",
                table: "PingSettings");

            migrationBuilder.DropIndex(
                name: "IX_PingSamples_OccurredAt_RowId",
                schema: "network",
                table: "PingSamples");

            migrationBuilder.DropIndex(
                name: "IX_PingRollups_WindowStart_RowId",
                schema: "network",
                table: "PingRollups");

            migrationBuilder.DropColumn(
                name: "RawRetentionDays",
                schema: "network",
                table: "PingSettings");

            migrationBuilder.DropColumn(
                name: "RollupRetentionDays",
                schema: "network",
                table: "PingSettings");
        }
    }
}

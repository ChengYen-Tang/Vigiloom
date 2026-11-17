using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.AddColumn<int>(
                name: "HealthDays",
                schema: "service",
                table: "TelemetryRetentionSettings",
                type: "int",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings",
                sql: "[LogDays] BETWEEN 1 AND 3650 AND [TraceDays] BETWEEN 1 AND 3650 AND [MetricRawDays] BETWEEN 1 AND 3650 AND [MetricRollupDays] BETWEEN [MetricRawDays] AND 3650 AND [HealthDays] BETWEEN 1 AND 3650");

            migrationBuilder.CreateIndex(
                name: "IX_HealthStateIntervals_EndedAt",
                schema: "service",
                table: "HealthStateIntervals",
                column: "EndedAt",
                filter: "[EndedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HealthObservations_ObservedAt",
                schema: "service",
                table: "HealthObservations",
                column: "ObservedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.DropIndex(
                name: "IX_HealthStateIntervals_EndedAt",
                schema: "service",
                table: "HealthStateIntervals");

            migrationBuilder.DropIndex(
                name: "IX_HealthObservations_ObservedAt",
                schema: "service",
                table: "HealthObservations");

            migrationBuilder.DropColumn(
                name: "HealthDays",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings",
                sql: "[LogDays] BETWEEN 1 AND 3650 AND [TraceDays] BETWEEN 1 AND 3650 AND [MetricRawDays] BETWEEN 1 AND 3650 AND [MetricRollupDays] BETWEEN [MetricRawDays] AND 3650");
        }
    }
}

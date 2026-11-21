using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.AddColumn<int>(
                name: "MetricRawDays",
                schema: "service",
                table: "TelemetryRetentionSettings",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "MetricRollupDays",
                schema: "service",
                table: "TelemetryRetentionSettings",
                type: "int",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MetricRetainedAfter",
                schema: "service",
                table: "Services",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings",
                sql: "[LogDays] BETWEEN 1 AND 3650 AND [TraceDays] BETWEEN 1 AND 3650 AND [MetricRawDays] BETWEEN 1 AND 3650 AND [MetricRollupDays] BETWEEN [MetricRawDays] AND 3650");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT COUNT(*) FROM [service].[TelemetryRetentionSettings] WITH (TABLOCKX, HOLDLOCK); IF EXISTS (SELECT 1 FROM [service].[Services] WHERE [MetricRetainedAfter] IS NOT NULL) THROW 51000, 'Cannot remove metric retention boundaries after data has been cleaned.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.DropColumn(
                name: "MetricRawDays",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.DropColumn(
                name: "MetricRollupDays",
                schema: "service",
                table: "TelemetryRetentionSettings");

            migrationBuilder.DropColumn(
                name: "MetricRetainedAfter",
                schema: "service",
                table: "Services");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TelemetryRetentionSettings_Days",
                schema: "service",
                table: "TelemetryRetentionSettings",
                sql: "[LogDays] BETWEEN 1 AND 3650 AND [TraceDays] BETWEEN 1 AND 3650");
        }
    }
}

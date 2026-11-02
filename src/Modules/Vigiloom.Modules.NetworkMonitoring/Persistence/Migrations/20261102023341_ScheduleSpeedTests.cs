using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleSpeedTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only adopt the new default for settings that an administrator has never changed.
            migrationBuilder.Sql("""
                UPDATE network.SpeedSettings
                SET Automatic = 1, MaxMiB = 2048,
                    NextAutomaticAt = DATEADD(hour, IntervalHours, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                WHERE Name = N'central' AND UpdatedBy IS NULL AND Automatic = 0 AND ConsentPolicy IS NULL;
                """);
            migrationBuilder.DropColumn(
                name: "ConsentPolicy",
                schema: "network",
                table: "SpeedSettings");

            migrationBuilder.DropColumn(
                name: "ConsentedAt",
                schema: "network",
                table: "SpeedSettings");

            migrationBuilder.DropColumn(
                name: "ConsentedBy",
                schema: "network",
                table: "SpeedSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConsentPolicy",
                schema: "network",
                table: "SpeedSettings",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsentedAt",
                schema: "network",
                table: "SpeedSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConsentedBy",
                schema: "network",
                table: "SpeedSettings",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}

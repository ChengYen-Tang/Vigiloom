using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpeedRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                schema: "network",
                table: "SpeedSettings",
                type: "int",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.AddColumn<long>(
                name: "RetentionDeletedTotal",
                schema: "network",
                table: "SpeedSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionLastBatchAt",
                schema: "network",
                table: "SpeedSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetentionLastBatchCount",
                schema: "network",
                table: "SpeedSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SpeedSettings_RetentionDays",
                schema: "network",
                table: "SpeedSettings",
                sql: "[RetentionDays] BETWEEN 1 AND 3650");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SpeedSettings_RetentionDays",
                schema: "network",
                table: "SpeedSettings");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                schema: "network",
                table: "SpeedSettings");

            migrationBuilder.DropColumn(
                name: "RetentionDeletedTotal",
                schema: "network",
                table: "SpeedSettings");

            migrationBuilder.DropColumn(
                name: "RetentionLastBatchAt",
                schema: "network",
                table: "SpeedSettings");

            migrationBuilder.DropColumn(
                name: "RetentionLastBatchCount",
                schema: "network",
                table: "SpeedSettings");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventorySettingsHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InventoryConfigurationEpoch",
                schema: "agent",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InventoryConfigurationJson",
                schema: "agent",
                table: "Agents",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InventoryConfigurationReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InventoryConfigurationSequence",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InventoryConfigurationEpoch",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InventoryConfigurationJson",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InventoryConfigurationReportedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InventoryConfigurationSequence",
                schema: "agent",
                table: "Agents");
        }
    }
}

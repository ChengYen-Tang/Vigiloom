using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentQueueStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QueueCapacityBatches",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueCapacityBytes",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueQuarantinedBatches",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueueReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueTotalBatches",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueTotalBytes",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueueCapacityBatches",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityBytes",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueQuarantinedBatches",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueReportedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueTotalBatches",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueTotalBytes",
                schema: "agent",
                table: "Agents");
        }
    }
}

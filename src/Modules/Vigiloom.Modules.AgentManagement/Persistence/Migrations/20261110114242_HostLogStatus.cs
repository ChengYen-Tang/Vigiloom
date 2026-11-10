using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogStatusJson",
                schema: "agent",
                table: "Agents",
                type: "nvarchar(max)",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogStatusReportEpoch",
                schema: "agent",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LogStatusReportSequence",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LogStatusReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogStatusJson",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LogStatusReportEpoch",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LogStatusReportSequence",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LogStatusReportedAt",
                schema: "agent",
                table: "Agents");
        }
    }
}

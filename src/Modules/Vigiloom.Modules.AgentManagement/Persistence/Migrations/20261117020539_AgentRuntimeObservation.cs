using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentRuntimeObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeIdentifier",
                schema: "agent",
                table: "Agents",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RuntimeReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeVersion",
                schema: "agent",
                table: "Agents",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeIdentifier",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "RuntimeReportedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "RuntimeVersion",
                schema: "agent",
                table: "Agents");
        }
    }
}

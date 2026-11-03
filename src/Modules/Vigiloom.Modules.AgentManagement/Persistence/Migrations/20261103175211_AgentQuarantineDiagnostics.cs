using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentQuarantineDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuarantineBadRequest",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineConflict",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineExpired",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineInvalidLocal",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineOther",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QuarantineReportEpoch",
                schema: "agent",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineReportSequence",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineTooLarge",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuarantineUnsupported",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuarantineBadRequest",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineConflict",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineExpired",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineInvalidLocal",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineOther",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineReportEpoch",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineReportSequence",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineTooLarge",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QuarantineUnsupported",
                schema: "agent",
                table: "Agents");
        }
    }
}

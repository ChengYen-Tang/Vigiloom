using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentReplayDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastReplayIssue",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastReplayIssueAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Agents_ReplayIssue",
                schema: "agent",
                table: "Agents",
                sql: "([LastReplayIssue] IS NULL AND [LastReplayIssueAt] IS NULL) OR ([LastReplayIssue] IS NOT NULL AND [LastReplayIssue] IN (1, 2, 3) AND [LastReplayIssueAt] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Agents_ReplayIssue",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LastReplayIssue",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LastReplayIssueAt",
                schema: "agent",
                table: "Agents");
        }
    }
}

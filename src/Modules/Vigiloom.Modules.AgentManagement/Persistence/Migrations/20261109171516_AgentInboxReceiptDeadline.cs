using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInboxReceiptDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetainUntil",
                schema: "agent",
                table: "ReceivedBatches",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [CleanupCutoverAt] IS NOT NULL) OR EXISTS (SELECT 1 FROM [agent].[Agents] WHERE [ReplayCleanedBefore] IS NOT NULL) THROW 51000, 'An activated inbox policy cannot be downgraded.', 1;");
            migrationBuilder.DropColumn(
                name: "RetainUntil",
                schema: "agent",
                table: "ReceivedBatches");
        }
    }
}

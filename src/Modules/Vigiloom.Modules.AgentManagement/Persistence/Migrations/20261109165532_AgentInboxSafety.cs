using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInboxSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CleanupCutoverAt",
                schema: "agent",
                table: "ReplayPolicies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrustedReceivedAt",
                schema: "agent",
                table: "ReceivedBatches",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReplayCleanedBefore",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removing these columns after cutover would erase the evidence needed to reject
            // batches whose receipts have already been cleaned. Deployment must also drain old receivers.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [CleanupCutoverAt] IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM [agent].[Agents] WITH (UPDLOCK, HOLDLOCK) WHERE [ReplayCleanedBefore] IS NOT NULL)
                    THROW 51000, 'An activated inbox cleanup protocol cannot be downgraded.', 1;
                """);
            migrationBuilder.DropColumn(
                name: "CleanupCutoverAt",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropColumn(
                name: "TrustedReceivedAt",
                schema: "agent",
                table: "ReceivedBatches");

            migrationBuilder.DropColumn(
                name: "ReplayCleanedBefore",
                schema: "agent",
                table: "Agents");
        }
    }
}

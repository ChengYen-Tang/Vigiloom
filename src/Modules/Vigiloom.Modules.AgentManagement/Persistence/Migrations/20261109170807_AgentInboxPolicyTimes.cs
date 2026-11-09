using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInboxPolicyTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InboxSettingsChangedAt",
                schema: "agent",
                table: "ReplayPolicies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_InboxTimes",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "([CleanupCutoverAt] IS NULL AND [InboxSettingsChangedAt] IS NULL) OR ([Phase] = 2 AND [CleanupCutoverAt] IS NOT NULL AND [InboxSettingsChangedAt] IS NOT NULL AND [InboxSettingsChangedAt] >= [CleanupCutoverAt] AND [CleanupCutoverAt] >= [EnforcedAt])");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_WindowWithinRetention",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "CAST([ReceiptRetentionDays] AS bigint) * 86400 >= CAST([ReplayWindowSeconds] AS bigint) + 300");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [InboxSettingsChangedAt] IS NOT NULL OR [CleanupCutoverAt] IS NOT NULL)
                    THROW 51000, 'An activated inbox policy cannot be downgraded.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_InboxTimes",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_WindowWithinRetention",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropColumn(
                name: "InboxSettingsChangedAt",
                schema: "agent",
                table: "ReplayPolicies");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInboxLegacyConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_LegacyDeadline",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "([CleanupCutoverAt] IS NULL AND [LegacyReceiptsRetainUntil] IS NULL) OR ([CleanupCutoverAt] IS NOT NULL AND [LegacyReceiptsRetainUntil] IS NOT NULL AND [LegacyReceiptsRetainUntil] >= [CleanupCutoverAt])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [CleanupCutoverAt] IS NOT NULL OR [LegacyReceiptsRetainUntil] IS NOT NULL) THROW 51000, 'An activated inbox policy cannot be downgraded.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_LegacyDeadline",
                schema: "agent",
                table: "ReplayPolicies");
        }
    }
}

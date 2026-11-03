using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentReplayFreshInstallation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_Phase",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.AddColumn<bool>(
                name: "FreshInstallation",
                schema: "agent",
                table: "ReplayPolicies",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_Phase",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "([Phase] = 0 AND [FreshInstallation] = 0 AND [ActivatedAt] IS NULL AND [GraceUntil] IS NULL AND [EnforcedAt] IS NULL) OR ([Phase] = 1 AND [FreshInstallation] = 0 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NULL) OR ([Phase] = 2 AND [ActivatedAt] IS NOT NULL AND [EnforcedAt] IS NOT NULL AND (([FreshInstallation] = 1 AND [GraceUntil] IS NULL AND [EnforcedAt] = [ActivatedAt]) OR ([FreshInstallation] = 0 AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] >= [GraceUntil])))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [Phase] <> 0) THROW 51000, 'An activated replay policy cannot be downgraded.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_Phase",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropColumn(
                name: "FreshInstallation",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_Phase",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "([Phase] = 0 AND [ActivatedAt] IS NULL AND [GraceUntil] IS NULL AND [EnforcedAt] IS NULL) OR ([Phase] = 1 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NULL) OR ([Phase] = 2 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NOT NULL AND [EnforcedAt] >= [GraceUntil])");
        }
    }
}

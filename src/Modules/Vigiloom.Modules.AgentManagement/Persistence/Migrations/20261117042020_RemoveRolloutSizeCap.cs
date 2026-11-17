using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRolloutSizeCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Rollouts_BatchSize",
                schema: "agent",
                table: "Rollouts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rollouts_BatchSize",
                schema: "agent",
                table: "Rollouts",
                sql: "[PilotSize] > 0 AND [BatchSize] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Rollouts_BatchSize",
                schema: "agent",
                table: "Rollouts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rollouts_BatchSize",
                schema: "agent",
                table: "Rollouts",
                sql: "[PilotSize] BETWEEN 1 AND 100 AND [BatchSize] BETWEEN 1 AND 100");
        }
    }
}

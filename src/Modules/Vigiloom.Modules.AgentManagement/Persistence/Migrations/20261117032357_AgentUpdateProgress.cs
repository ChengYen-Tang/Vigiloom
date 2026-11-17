using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdateProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UpdateReceipts_Result",
                schema: "agent",
                table: "UpdateReceipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UpdateReceipts_Result",
                schema: "agent",
                table: "UpdateReceipts",
                sql: "[Sequence] > 0 AND [Result] IN (1, 2, 3, 4, 5, 6, 7, 8, 9) AND ([Failure] IS NULL OR [Failure] IN (1, 2, 3)) AND ([Result] IN (2, 3) OR ([RetryAllowed] = 0 AND [Failure] IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UpdateReceipts_Result",
                schema: "agent",
                table: "UpdateReceipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UpdateReceipts_Result",
                schema: "agent",
                table: "UpdateReceipts",
                sql: "[Sequence] > 0 AND [Result] IN (1, 2, 3) AND ([Failure] IS NULL OR [Failure] IN (1, 2, 3)) AND ([Result] <> 1 OR ([RetryAllowed] = 0 AND [Failure] IS NULL))");
        }
    }
}

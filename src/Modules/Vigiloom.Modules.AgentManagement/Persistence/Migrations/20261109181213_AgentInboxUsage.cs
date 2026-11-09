using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInboxUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InboxReceiptCount",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InboxReceiptPayloadBytes",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Agents_InboxUsage",
                schema: "agent",
                table: "Agents",
                sql: "([InboxReceiptCount] IS NULL AND [InboxReceiptPayloadBytes] IS NULL) OR ([InboxReceiptCount] >= 0 AND [InboxReceiptPayloadBytes] >= 0 AND [InboxReceiptCount] IS NOT NULL AND [InboxReceiptPayloadBytes] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[Agents] WHERE [InboxReceiptCount] IS NOT NULL OR [ReplayCleanedBefore] IS NOT NULL) THROW 51000, 'Initialized inbox usage cannot be downgraded.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_Agents_InboxUsage",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InboxReceiptCount",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InboxReceiptPayloadBytes",
                schema: "agent",
                table: "Agents");
        }
    }
}

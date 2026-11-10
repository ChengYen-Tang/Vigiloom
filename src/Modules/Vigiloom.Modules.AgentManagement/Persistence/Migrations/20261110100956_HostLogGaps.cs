using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GapAffectedCount",
                schema: "agent",
                table: "HostLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GapReason",
                schema: "agent",
                table: "HostLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_HostLogs_Gap",
                schema: "agent",
                table: "HostLogs",
                sql: "([GapReason] IS NULL AND [GapAffectedCount] IS NULL) OR\n([GapReason] IS NOT NULL AND [GapReason] BETWEEN 1 AND 5 AND\n ([GapAffectedCount] IS NULL OR [GapAffectedCount] > 0) AND [Content] = N'' AND\n [Provider] IS NULL AND [EventId] IS NULL AND [Level] IS NULL AND [User] IS NULL AND [Process] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[HostLogs] WITH (UPDLOCK, HOLDLOCK) WHERE [GapReason] IS NOT NULL)
                    THROW 51000, 'Cannot remove host log gap data during downgrade.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_HostLogs_Gap",
                schema: "agent",
                table: "HostLogs");

            migrationBuilder.DropColumn(
                name: "GapAffectedCount",
                schema: "agent",
                table: "HostLogs");

            migrationBuilder.DropColumn(
                name: "GapReason",
                schema: "agent",
                table: "HostLogs");
        }
    }
}

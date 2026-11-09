using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostMetricKindIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_AgentId_Kind_OccurredAt_RowId",
                schema: "agent",
                table: "HostMetrics",
                columns: new[] { "AgentId", "Kind", "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HostMetrics_AgentId_Kind_OccurredAt_RowId",
                schema: "agent",
                table: "HostMetrics");
        }
    }
}

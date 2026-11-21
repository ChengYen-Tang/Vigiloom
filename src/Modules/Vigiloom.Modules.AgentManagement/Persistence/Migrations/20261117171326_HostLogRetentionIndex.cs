using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_ReceivedAt_RowId",
                schema: "agent",
                table: "HostLogs",
                columns: new[] { "ReceivedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HostLogs_ReceivedAt_RowId",
                schema: "agent",
                table: "HostLogs");
        }
    }
}

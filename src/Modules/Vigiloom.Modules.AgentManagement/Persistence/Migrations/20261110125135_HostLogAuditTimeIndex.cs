using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogAuditTimeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HostLogSourceAudits_OccurredAt_RowId",
                schema: "agent",
                table: "HostLogSourceAudits",
                columns: new[] { "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HostLogSourceAudits_OccurredAt_RowId",
                schema: "agent",
                table: "HostLogSourceAudits");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditFeedTimeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SettingAudits_OccurredAt_RowId",
                schema: "agent",
                table: "SettingAudits",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentAudits_OccurredAt_RowId",
                schema: "agent",
                table: "AgentAudits",
                columns: new[] { "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SettingAudits_OccurredAt_RowId",
                schema: "agent",
                table: "SettingAudits");

            migrationBuilder.DropIndex(
                name: "IX_AgentAudits_OccurredAt_RowId",
                schema: "agent",
                table: "AgentAudits");
        }
    }
}

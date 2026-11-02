using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentAuditRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AgentAudits_AgentId",
                schema: "agent",
                table: "AgentAudits",
                column: "AgentId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentAudits_Agents_AgentId",
                schema: "agent",
                table: "AgentAudits",
                column: "AgentId",
                principalSchema: "agent",
                principalTable: "Agents",
                principalColumn: "Id");

            // Resources owns the device entity; migration ordering establishes the cross-schema FK.
            migrationBuilder.AddForeignKey(
                name: "FK_AgentAudits_Devices_DeviceId", schema: "agent", table: "AgentAudits", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_AgentAudits_Devices_DeviceId", schema: "agent", table: "AgentAudits");
            migrationBuilder.DropForeignKey(
                name: "FK_AgentAudits_Agents_AgentId",
                schema: "agent",
                table: "AgentAudits");

            migrationBuilder.DropIndex(
                name: "IX_AgentAudits_AgentId",
                schema: "agent",
                table: "AgentAudits");
        }
    }
}

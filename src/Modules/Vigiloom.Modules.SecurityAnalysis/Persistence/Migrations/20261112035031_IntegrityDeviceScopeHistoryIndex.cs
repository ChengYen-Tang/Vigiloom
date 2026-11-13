using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrityDeviceScopeHistoryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IntegrityChanges_DeviceId_ScopeId_ObservedAt_RowId",
                schema: "security",
                table: "IntegrityChanges",
                columns: new[] { "DeviceId", "ScopeId", "ObservedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntegrityChanges_DeviceId_ScopeId_ObservedAt_RowId",
                schema: "security",
                table: "IntegrityChanges");
        }
    }
}

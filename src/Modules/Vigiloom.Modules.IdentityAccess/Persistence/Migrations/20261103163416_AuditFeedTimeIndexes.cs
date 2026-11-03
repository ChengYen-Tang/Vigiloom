using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.IdentityAccess.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditFeedTimeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ApiCredentialAuditEvents_OccurredAt_RowId",
                schema: "identity",
                table: "ApiCredentialAuditEvents",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAuditEvents_OccurredAt_RowId",
                schema: "identity",
                table: "AccountAuditEvents",
                columns: new[] { "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiCredentialAuditEvents_OccurredAt_RowId",
                schema: "identity",
                table: "ApiCredentialAuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AccountAuditEvents_OccurredAt_RowId",
                schema: "identity",
                table: "AccountAuditEvents");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditRetentionSettings",
                schema: "operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    Days = table.Column<int>(type: "int", nullable: false, defaultValue: 365),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRetentionSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_AuditRetentionSettings_Days", "[Days] BETWEEN 1 AND 3650");
                    table.CheckConstraint("CK_AuditRetentionSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_AuditRetentionSettings_Slot", "[Slot] = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditRetentionSettings_RowId",
                schema: "operations",
                table: "AuditRetentionSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRetentionSettings_Slot",
                schema: "operations",
                table: "AuditRetentionSettings",
                column: "Slot",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditRetentionSettings",
                schema: "operations");
        }
    }
}

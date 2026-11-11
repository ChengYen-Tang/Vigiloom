using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventorySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventorySettings",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ScanIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_InventorySettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_InventorySettings_Interval", "[ScanIntervalSeconds] IS NULL OR [ScanIntervalSeconds] BETWEEN 900 AND 604800");
                });

            migrationBuilder.CreateTable(
                name: "InventorySettingAudits",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScanIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    EffectiveScanIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySettingAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_InventorySettingAudits_InventorySettings_SettingId",
                        column: x => x.SettingId,
                        principalSchema: "security",
                        principalTable: "InventorySettings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySettingAudits_DeviceId_OccurredAt_RowId",
                schema: "security",
                table: "InventorySettingAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySettingAudits_OccurredAt_RowId",
                schema: "security",
                table: "InventorySettingAudits",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySettingAudits_SettingId",
                schema: "security",
                table: "InventorySettingAudits",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_InventorySettings_DeviceId",
                schema: "security",
                table: "InventorySettings",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventorySettings_RowId",
                schema: "security",
                table: "InventorySettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventorySettings] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[InventorySettingAudits] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Inventory settings or audit history prevent downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "InventorySettingAudits",
                schema: "security");

            migrationBuilder.DropTable(
                name: "InventorySettings",
                schema: "security");
        }
    }
}

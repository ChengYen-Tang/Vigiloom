using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryChanges",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryChanges", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_InventoryChanges_Content", "([Kind] = 1 AND [BeforeJson] IS NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 2 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 3 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NULL)");
                    table.CheckConstraint("CK_InventoryChanges_Values", "[Generation] > 0 AND [Source] BETWEEN 1 AND 10 AND [Kind] BETWEEN 1 AND 3");
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryChanges_DeviceId_Source_ObservedAt_RowId",
                schema: "security",
                table: "InventoryChanges",
                columns: new[] { "DeviceId", "Source", "ObservedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventoryChanges] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Inventory change history prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "InventoryChanges",
                schema: "security");
        }
    }
}

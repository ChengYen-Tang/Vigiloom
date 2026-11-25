using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicTemplates",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_CI_AS_SC"),
                    ShowLatency = table.Column<bool>(type: "bit", nullable: false),
                    ShowObservedAt = table.Column<bool>(type: "bit", nullable: false),
                    HistoryDays = table.Column<int>(type: "int", nullable: false),
                    HistorySince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicTemplates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_PublicTemplates_DefaultActive", "[IsDefault] = 0 OR [IsRetired] = 0");
                    table.CheckConstraint("CK_PublicTemplates_HistoryPeriod", "([HistoryDays] = 0 AND [HistorySince] IS NULL) OR ([HistoryDays] BETWEEN 1 AND 7 AND [HistorySince] IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicTemplates_IsDefault",
                schema: "resources",
                table: "PublicTemplates",
                column: "IsDefault",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PublicTemplates_Name",
                schema: "resources",
                table: "PublicTemplates",
                column: "Name",
                unique: true,
                filter: "[IsRetired] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PublicTemplates_RowId",
                schema: "resources",
                table: "PublicTemplates",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicTemplates",
                schema: "resources");
        }
    }
}

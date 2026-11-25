using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PingTransitionCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PingTransitionCursors",
                schema: "operations",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingTransitionCursors", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PingTransitionCursors_DeviceId_Target",
                schema: "operations",
                table: "PingTransitionCursors",
                columns: new[] { "DeviceId", "Target" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PingTransitionCursors",
                schema: "operations");
        }
    }
}

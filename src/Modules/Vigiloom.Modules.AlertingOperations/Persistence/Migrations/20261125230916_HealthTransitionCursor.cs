using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthTransitionCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthTransitionCursors",
                schema: "operations",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthTransitionCursors", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthTransitionCursors_TargetId",
                schema: "operations",
                table: "HealthTransitionCursors",
                column: "TargetId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthTransitionCursors",
                schema: "operations");
        }
    }
}

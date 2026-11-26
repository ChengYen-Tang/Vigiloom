using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NetworkDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetworkDiagnostics",
                schema: "operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PingSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PingFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PingFailures = table.Column<int>(type: "int", nullable: false),
                    PingFailureKind = table.Column<short>(type: "smallint", nullable: false),
                    SpeedSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SpeedFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SpeedFailures = table.Column<int>(type: "int", nullable: false),
                    SpeedFailureKind = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetworkDiagnostics", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetworkDiagnostics_LeaseUntil",
                schema: "operations",
                table: "NetworkDiagnostics",
                column: "LeaseUntil");

            migrationBuilder.CreateIndex(
                name: "IX_NetworkDiagnostics_RowId",
                schema: "operations",
                table: "NetworkDiagnostics",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetworkDiagnostics",
                schema: "operations");
        }
    }
}

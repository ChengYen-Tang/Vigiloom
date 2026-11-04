using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CollectorRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Collectors",
                schema: "service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MonitoringServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collectors", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Collectors_Services_MonitoringServiceId",
                        column: x => x.MonitoringServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectorRoutes",
                schema: "service",
                columns: table => new
                {
                    CollectorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Exporter = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExporterBytes = table.Column<byte[]>(type: "varbinary(256)", nullable: false, computedColumnSql: "CONVERT(varbinary(256), [Exporter])", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectorRoutes", x => new { x.CollectorId, x.SourceId })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_CollectorRoutes_Collectors_CollectorId",
                        column: x => x.CollectorId,
                        principalSchema: "service",
                        principalTable: "Collectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectorRoutes_Sources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "service",
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectorRoutes_CollectorId_ExporterBytes",
                schema: "service",
                table: "CollectorRoutes",
                columns: new[] { "CollectorId", "ExporterBytes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CollectorRoutes_SourceId",
                schema: "service",
                table: "CollectorRoutes",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Collectors_MonitoringServiceId",
                schema: "service",
                table: "Collectors",
                column: "MonitoringServiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Collectors_RowId",
                schema: "service",
                table: "Collectors",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectorRoutes",
                schema: "service");

            migrationBuilder.DropTable(
                name: "Collectors",
                schema: "service");
        }
    }
}

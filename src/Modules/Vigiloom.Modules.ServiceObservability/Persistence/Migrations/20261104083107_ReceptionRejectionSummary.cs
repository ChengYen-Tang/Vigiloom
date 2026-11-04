using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceptionRejectionSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceptionRejections",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ObservedCount = table.Column<long>(type: "bigint", nullable: false),
                    FirstRecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExampleIngress = table.Column<byte>(type: "tinyint", nullable: true),
                    ExampleSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceptionRejections", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_ReceptionRejections_Kind", "[Kind] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_ReceptionRejections_Sources_ExampleSourceId",
                        column: x => x.ExampleSourceId,
                        principalSchema: "service",
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionRejections_ExampleSourceId",
                schema: "service",
                table: "ReceptionRejections",
                column: "ExampleSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceptionRejections_Kind",
                schema: "service",
                table: "ReceptionRejections",
                column: "Kind",
                unique: true);

            migrationBuilder.Sql("INSERT INTO [service].[ReceptionRejections] ([Kind], [ObservedCount]) VALUES (1, 0), (2, 0), (3, 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceptionRejections",
                schema: "service");
        }
    }
}

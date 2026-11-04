using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServiceInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Services_Id_SourceId",
                schema: "service",
                table: "Services",
                columns: new[] { "Id", "SourceId" });

            migrationBuilder.CreateTable(
                name: "Instances",
                schema: "service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalInstanceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExternalInstanceIdBytes = table.Column<byte[]>(type: "varbinary(512)", nullable: false, computedColumnSql: "CONVERT(varbinary(512), [ExternalInstanceId])", stored: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Instances_Services_ServiceId_SourceScopeId",
                        columns: x => new { x.ServiceId, x.SourceScopeId },
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumns: new[] { "Id", "SourceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Id_ServiceId",
                schema: "service",
                table: "Instances",
                columns: new[] { "Id", "ServiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_RowId",
                schema: "service",
                table: "Instances",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_ServiceId_SourceScopeId_ExternalInstanceIdBytes",
                schema: "service",
                table: "Instances",
                columns: new[] { "ServiceId", "SourceScopeId", "ExternalInstanceIdBytes" },
                unique: true,
                filter: "[RetiredAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_ServiceId_SourceScopeId_ExternalInstanceIdBytes_RetiredAt",
                schema: "service",
                table: "Instances",
                columns: new[] { "ServiceId", "SourceScopeId", "ExternalInstanceIdBytes", "RetiredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instances",
                schema: "service");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Services_Id_SourceId",
                schema: "service",
                table: "Services");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostLogSources",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    TextMode = table.Column<int>(type: "int", nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostLogSources", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSources_DeviceId_RetiredAt",
                schema: "agent",
                table: "HostLogSources",
                columns: new[] { "DeviceId", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSources_RowId",
                schema: "agent",
                table: "HostLogSources",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            // Resources owns the Device entity in a separate DbContext.
            migrationBuilder.AddForeignKey(name: "FK_HostLogSources_Devices_DeviceId", schema: "agent", table: "HostLogSources",
                column: "DeviceId", principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[HostLogSources] WITH (UPDLOCK, HOLDLOCK))
                    THROW 51000, 'Cannot remove host log source settings during downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "HostLogSources",
                schema: "agent");
        }
    }
}

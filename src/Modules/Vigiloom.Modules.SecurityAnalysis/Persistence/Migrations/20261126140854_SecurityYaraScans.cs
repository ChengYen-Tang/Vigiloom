using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityYaraScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YaraScans",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Rules = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: false),
                    MaximumBytes = table.Column<long>(type: "bigint", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    Output = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: true),
                    EngineVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OutputTruncated = table.Column<bool>(type: "bit", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YaraScans", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YaraScans_AgentId_CompletedAt_RequestedAt",
                schema: "security",
                table: "YaraScans",
                columns: new[] { "AgentId", "CompletedAt", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_YaraScans_DeviceId_RequestedAt_RowId",
                schema: "security",
                table: "YaraScans",
                columns: new[] { "DeviceId", "RequestedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_YaraScans_RowId",
                schema: "security",
                table: "YaraScans",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YaraScans",
                schema: "security");
        }
    }
}

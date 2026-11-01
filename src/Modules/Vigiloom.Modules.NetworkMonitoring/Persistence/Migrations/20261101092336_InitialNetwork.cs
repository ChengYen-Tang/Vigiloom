using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialNetwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "network");

            migrationBuilder.CreateTable(
                name: "PingSamples",
                schema: "network",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    LatencyMilliseconds = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingSamples", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            // Cross-module references are owned by Network; no Resources CLR dependency is required.
            migrationBuilder.AddForeignKey(
                name: "FK_PingSamples_Devices_DeviceId",
                schema: "network",
                table: "PingSamples",
                column: "DeviceId",
                principalSchema: "resources",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.CreateIndex(
                name: "IX_PingSamples_DeviceId_OccurredAt_RowId",
                schema: "network",
                table: "PingSamples",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PingSamples",
                schema: "network");
        }
    }
}

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

            // The legacy shared database has Resources locally; an independent Network database does not.
            migrationBuilder.Sql("IF OBJECT_ID(N'[resources].[Devices]', N'U') IS NOT NULL ALTER TABLE [network].[PingSamples] ADD CONSTRAINT [FK_PingSamples_Devices_DeviceId] FOREIGN KEY ([DeviceId]) REFERENCES [resources].[Devices] ([Id]);");

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

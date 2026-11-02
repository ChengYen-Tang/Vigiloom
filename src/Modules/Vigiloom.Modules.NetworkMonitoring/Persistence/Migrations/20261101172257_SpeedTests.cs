using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpeedTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpeedRuns",
                schema: "network",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Location = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Automatic = table.Column<bool>(type: "bit", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    MaxMiB = table.Column<int>(type: "int", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Server = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Isp = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    DownloadMbps = table.Column<double>(type: "float", nullable: true),
                    UploadMbps = table.Column<double>(type: "float", nullable: true),
                    LatencyMilliseconds = table.Column<double>(type: "float", nullable: true),
                    Bytes = table.Column<long>(type: "bigint", nullable: true),
                    DiagnosticCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CancellationRequested = table.Column<bool>(type: "bit", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedRuns", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_SpeedRuns_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "SpeedSettings",
                schema: "network",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Automatic = table.Column<bool>(type: "bit", nullable: false),
                    IntervalHours = table.Column<int>(type: "int", nullable: false),
                    MaxMiB = table.Column<int>(type: "int", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    ConsentPolicy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ConsentedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsentedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAutomaticAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_SpeedSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedRuns_Location",
                schema: "network",
                table: "SpeedRuns",
                column: "Location",
                unique: true,
                filter: "[State] < 2");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedRuns_RequestedAt_RowId",
                schema: "network",
                table: "SpeedRuns",
                columns: new[] { "RequestedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedRuns_RequestedBy_RequestId",
                schema: "network",
                table: "SpeedRuns",
                columns: new[] { "RequestedBy", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedRuns_RowId",
                schema: "network",
                table: "SpeedRuns",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedSettings_Name",
                schema: "network",
                table: "SpeedSettings",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedSettings_RowId",
                schema: "network",
                table: "SpeedSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpeedRuns",
                schema: "network");

            migrationBuilder.DropTable(
                name: "SpeedSettings",
                schema: "network");
        }
    }
}

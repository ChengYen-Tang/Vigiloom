using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricRollupWindows",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InputRevision = table.Column<long>(type: "bigint", nullable: false),
                    ProjectedRevision = table.Column<long>(type: "bigint", nullable: true),
                    ProjectionVersion = table.Column<int>(type: "int", nullable: true),
                    ProjectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsComplete = table.Column<bool>(type: "bit", nullable: false),
                    RawPruned = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollupWindows", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_MetricRollupWindows_Aligned", "DATEPART(MINUTE, [WindowStart]) % 5 = 0 AND DATEPART(SECOND, [WindowStart]) = 0 AND DATEPART(NANOSECOND, [WindowStart]) = 0");
                    table.CheckConstraint("CK_MetricRollupWindows_Revisions", "[InputRevision] > 0 AND (([ProjectedRevision] IS NULL AND [ProjectionVersion] IS NULL AND [ProjectedAt] IS NULL AND [IsComplete] = 0 AND [RawPruned] = 0) OR ([ProjectedRevision] IS NOT NULL AND [ProjectedRevision] > 0 AND [ProjectedRevision] <= [InputRevision] AND [ProjectionVersion] IS NOT NULL AND [ProjectionVersion] > 0 AND [ProjectedAt] IS NOT NULL))");
                    table.CheckConstraint("CK_MetricRollupWindows_Utc", "DATEPART(TZOFFSET, [WindowStart]) = 0 AND ([ProjectedAt] IS NULL OR DATEPART(TZOFFSET, [ProjectedAt]) = 0)");
                    table.ForeignKey(
                        name: "FK_MetricRollupWindows_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MetricRollupSeries",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WindowRowId = table.Column<long>(type: "bigint", nullable: false),
                    IdentityHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    IdentityBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CollisionOrdinal = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<byte>(type: "tinyint", nullable: false),
                    QualityFlags = table.Column<long>(type: "bigint", nullable: false),
                    RequiresRaw = table.Column<bool>(type: "bit", nullable: false),
                    ResultVersion = table.Column<int>(type: "int", nullable: false),
                    InputCount = table.Column<int>(type: "int", nullable: false),
                    InputBytes = table.Column<long>(type: "bigint", nullable: false),
                    ResultPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollupSeries", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_MetricRollupSeries_Bounds", "DATALENGTH([IdentityHash]) = 32 AND DATALENGTH([IdentityBytes]) BETWEEN 1 AND 524288 AND ([ResultPayload] IS NULL OR DATALENGTH([ResultPayload]) <= 16777216) AND [CollisionOrdinal] >= 0 AND [Mode] BETWEEN 1 AND 6 AND [QualityFlags] >= 0 AND [ResultVersion] > 0 AND [InputCount] > 0 AND [InputBytes] >= 0");
                    table.CheckConstraint("CK_MetricRollupSeries_Preservation", "([Mode] <> 6 OR [RequiresRaw] = 1) AND ([RequiresRaw] = 1 OR [ResultPayload] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_MetricRollupSeries_MetricRollupWindows_WindowRowId",
                        column: x => x.WindowRowId,
                        principalSchema: "service",
                        principalTable: "MetricRollupWindows",
                        principalColumn: "RowId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupSeries_WindowRowId_IdentityHash_CollisionOrdinal",
                schema: "service",
                table: "MetricRollupSeries",
                columns: new[] { "WindowRowId", "IdentityHash", "CollisionOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupWindows_ServiceId_WindowStart",
                schema: "service",
                table: "MetricRollupWindows",
                columns: new[] { "ServiceId", "WindowStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [service].[MetricRollupWindows] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [service].[MetricRollupSeries] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot remove metric rollup tables containing data.', 1;
                """);
            migrationBuilder.DropTable(
                name: "MetricRollupSeries",
                schema: "service");

            migrationBuilder.DropTable(
                name: "MetricRollupWindows",
                schema: "service");
        }
    }
}

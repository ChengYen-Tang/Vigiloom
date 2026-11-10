using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollupHistogramModes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MetricRollupSeries_Bounds",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MetricRollupSeries_Bounds",
                schema: "service",
                table: "MetricRollupSeries",
                sql: "DATALENGTH([IdentityHash]) = 32 AND DATALENGTH([IdentityBytes]) BETWEEN 1 AND 524288 AND ([ResultPayload] IS NULL OR DATALENGTH([ResultPayload]) <= 16777216) AND [CollisionOrdinal] >= 0 AND [Mode] BETWEEN 1 AND 7 AND [QualityFlags] >= 0 AND [ResultVersion] > 0 AND [InputCount] > 0 AND [InputBytes] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MetricRollupSeries_Bounds",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MetricRollupSeries_Bounds",
                schema: "service",
                table: "MetricRollupSeries",
                sql: "DATALENGTH([IdentityHash]) = 32 AND DATALENGTH([IdentityBytes]) BETWEEN 1 AND 524288 AND ([ResultPayload] IS NULL OR DATALENGTH([ResultPayload]) <= 16777216) AND [CollisionOrdinal] >= 0 AND [Mode] BETWEEN 1 AND 6 AND [QualityFlags] >= 0 AND [ResultVersion] > 0 AND [InputCount] > 0 AND [InputBytes] >= 0");
        }
    }
}

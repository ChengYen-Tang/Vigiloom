using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollupInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MetricRollupSeries_MetricRollupWindows_WindowRowId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.AddColumn<Guid>(
                name: "InstanceId",
                schema: "service",
                table: "MetricRollupSeries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServiceId",
                schema: "service",
                table: "MetricRollupSeries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Version 1 canonical identity is: version byte, source Guid bytes, service Guid
            // bytes, presence byte, then optional instance Guid bytes. Reject unexpected or
            // inconsistent data rather than assigning it to a different resource.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [service].[MetricRollupSeries] AS s WITH (TABLOCKX, HOLDLOCK)
                    JOIN [service].[MetricRollupWindows] AS w ON w.[RowId] = s.[WindowRowId]
                    JOIN [service].[Services] AS service ON service.[Id] = w.[ServiceId]
                    WHERE DATALENGTH(s.[IdentityBytes]) < 52 OR SUBSTRING(s.[IdentityBytes], 1, 1) <> 0x01
                       OR HASHBYTES('SHA2_256', s.[IdentityBytes]) <> s.[IdentityHash]
                       OR SUBSTRING(s.[IdentityBytes], 34, 1) NOT IN (0x00, 0x01)
                       OR (SUBSTRING(s.[IdentityBytes], 34, 1) = 0x01 AND DATALENGTH(s.[IdentityBytes]) < 68)
                       OR TRY_CONVERT(uniqueidentifier, SUBSTRING(s.[IdentityBytes], 2, 16)) IS NULL
                       OR TRY_CONVERT(uniqueidentifier, SUBSTRING(s.[IdentityBytes], 2, 16)) <> service.[SourceId]
                       OR TRY_CONVERT(uniqueidentifier, SUBSTRING(s.[IdentityBytes], 18, 16)) IS NULL
                       OR TRY_CONVERT(uniqueidentifier, SUBSTRING(s.[IdentityBytes], 18, 16)) <> w.[ServiceId]
                ) THROW 51006, 'Cannot migrate metric rollup identity; restore or repair the inconsistent data.', 1;
                UPDATE s SET [ServiceId] = w.[ServiceId],
                    [InstanceId] = CASE WHEN SUBSTRING(s.[IdentityBytes], 34, 1) = 0x01
                        THEN CONVERT(uniqueidentifier, SUBSTRING(s.[IdentityBytes], 35, 16)) ELSE NULL END
                FROM [service].[MetricRollupSeries] AS s
                JOIN [service].[MetricRollupWindows] AS w ON w.[RowId] = s.[WindowRowId];
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MetricRollupWindows_RowId_ServiceId",
                schema: "service",
                table: "MetricRollupWindows",
                columns: new[] { "RowId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupSeries_InstanceId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries",
                columns: new[] { "InstanceId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupSeries_WindowRowId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries",
                columns: new[] { "WindowRowId", "ServiceId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MetricRollupSeries_Instances_InstanceId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries",
                columns: new[] { "InstanceId", "ServiceId" },
                principalSchema: "service",
                principalTable: "Instances",
                principalColumns: new[] { "Id", "ServiceId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MetricRollupSeries_MetricRollupWindows_WindowRowId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries",
                columns: new[] { "WindowRowId", "ServiceId" },
                principalSchema: "service",
                principalTable: "MetricRollupWindows",
                principalColumns: new[] { "RowId", "ServiceId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [service].[MetricRollupSeries] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot remove metric rollup instance associations while results exist.', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_MetricRollupSeries_Instances_InstanceId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.DropForeignKey(
                name: "FK_MetricRollupSeries_MetricRollupWindows_WindowRowId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MetricRollupWindows_RowId_ServiceId",
                schema: "service",
                table: "MetricRollupWindows");

            migrationBuilder.DropIndex(
                name: "IX_MetricRollupSeries_InstanceId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.DropIndex(
                name: "IX_MetricRollupSeries_WindowRowId_ServiceId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.DropColumn(
                name: "ServiceId",
                schema: "service",
                table: "MetricRollupSeries");

            migrationBuilder.AddForeignKey(
                name: "FK_MetricRollupSeries_MetricRollupWindows_WindowRowId",
                schema: "service",
                table: "MetricRollupSeries",
                column: "WindowRowId",
                principalSchema: "service",
                principalTable: "MetricRollupWindows",
                principalColumn: "RowId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

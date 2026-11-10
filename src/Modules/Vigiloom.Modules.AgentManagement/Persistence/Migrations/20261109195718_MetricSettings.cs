using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricSettings",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HostIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    ProcessIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_MetricSettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "MetricSettingAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HostIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    ProcessIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    EffectiveHostIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveProcessIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSettingAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_MetricSettingAudits_MetricSettings_SettingId",
                        column: x => x.SettingId,
                        principalSchema: "agent",
                        principalTable: "MetricSettings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettingAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "MetricSettingAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettingAudits_OccurredAt_RowId",
                schema: "agent",
                table: "MetricSettingAudits",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettingAudits_SettingId",
                schema: "agent",
                table: "MetricSettingAudits",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettings_DeviceId",
                schema: "agent",
                table: "MetricSettings",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettings_RowId",
                schema: "agent",
                table: "MetricSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(name: "FK_MetricSettings_Devices_DeviceId", schema: "agent", table: "MetricSettings", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
            migrationBuilder.AddForeignKey(name: "FK_MetricSettingAudits_Devices_DeviceId", schema: "agent", table: "MetricSettingAudits", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[MetricSettings] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [agent].[MetricSettingAudits] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot remove non-empty Agent metric settings or audit data.', 1;
                """);
            migrationBuilder.DropForeignKey(name: "FK_MetricSettingAudits_Devices_DeviceId", schema: "agent", table: "MetricSettingAudits");
            migrationBuilder.DropForeignKey(name: "FK_MetricSettings_Devices_DeviceId", schema: "agent", table: "MetricSettings");
            migrationBuilder.DropTable(
                name: "MetricSettingAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "MetricSettings",
                schema: "agent");
        }
    }
}

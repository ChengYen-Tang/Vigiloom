using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.DeploymentMigrations
{
    /// <inheritdoc />
    public partial class DeploymentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "resources");

            migrationBuilder.CreateTable(
                name: "DeploymentLinks",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLinks", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_DeploymentLinks_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [ServiceInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [AttestedBy] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_DeploymentLinks_Period", "[EndedAt] IS NULL OR [EndedAt] >= [StartedAt]");
                    // Both principals are owned by other contexts and are migrated first.
                    table.ForeignKey("FK_DeploymentLinks_Devices_DeviceId", x => x.DeviceId,
                        principalSchema: "resources", principalTable: "Devices", principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey("FK_DeploymentLinks_Instances_ServiceInstanceId", x => x.ServiceInstanceId,
                        principalSchema: "service", principalTable: "Instances", principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLinks_DeviceId_ServiceInstanceId",
                schema: "resources",
                table: "DeploymentLinks",
                columns: new[] { "DeviceId", "ServiceInstanceId" },
                unique: true,
                filter: "[EndedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLinks_RowId",
                schema: "resources",
                table: "DeploymentLinks",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLinks_ServiceInstanceId_EndedAt",
                schema: "resources",
                table: "DeploymentLinks",
                columns: new[] { "ServiceInstanceId", "EndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [resources].[DeploymentLinks] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot remove recorded deployment relationships.', 1;");
            migrationBuilder.DropTable(
                name: "DeploymentLinks",
                schema: "resources");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentVersionAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                schema: "agent",
                table: "AgentAudits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousPackageId",
                schema: "agent",
                table: "AgentAudits",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VersionAssignments",
                schema: "agent",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionAssignments", x => x.AgentId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_VersionAssignments_Target", "([PackageId] IS NULL AND [UpdateId] IS NULL) OR ([PackageId] IS NOT NULL AND [UpdateId] IS NOT NULL AND [UpdateId] <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_VersionAssignments_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VersionAssignments_Packages_PackageId",
                        column: x => x.PackageId,
                        principalSchema: "agent",
                        principalTable: "Packages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_VersionAssignments_PackageId",
                schema: "agent",
                table: "VersionAssignments",
                column: "PackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VersionAssignments",
                schema: "agent");

            migrationBuilder.DropColumn(
                name: "PackageId",
                schema: "agent",
                table: "AgentAudits");

            migrationBuilder.DropColumn(
                name: "PreviousPackageId",
                schema: "agent",
                table: "AgentAudits");
        }
    }
}

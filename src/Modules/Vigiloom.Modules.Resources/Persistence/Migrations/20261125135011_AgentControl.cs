using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Verifier = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    PendingVerifier = table.Column<byte[]>(type: "binary(32)", nullable: true),
                    PendingUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ResourceAgents_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_Agents_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "resources",
                        principalTable: "Devices",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AgentEnrollments",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Verifier = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEnrollments", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ResourceEnrollments_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "resources",
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "resources",
                        principalTable: "Devices",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AgentUpdateTargets",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RuntimeIdentifier = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ManifestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentUpdateTargets", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ResourceUpdateTargets_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_AgentUpdateTargets_AgentPackages_PackageId",
                        column: x => x.PackageId,
                        principalSchema: "resources",
                        principalTable: "AgentPackages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AgentUpdateTargets_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "resources",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AgentVersionAssignments",
                schema: "resources",
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
                    table.PrimaryKey("PK_AgentVersionAssignments", x => x.AgentId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_ResourceVersionAssignments_Target", "([PackageId] IS NULL AND [UpdateId] IS NULL) OR ([PackageId] IS NOT NULL AND [UpdateId] IS NOT NULL AND [UpdateId] <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_AgentVersionAssignments_AgentPackages_PackageId",
                        column: x => x.PackageId,
                        principalSchema: "resources",
                        principalTable: "AgentPackages",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AgentVersionAssignments_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "resources",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_AgentId",
                schema: "resources",
                table: "AgentEnrollments",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_DeviceId",
                schema: "resources",
                table: "AgentEnrollments",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_RowId",
                schema: "resources",
                table: "AgentEnrollments",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_DeviceId",
                schema: "resources",
                table: "Agents",
                column: "DeviceId",
                unique: true,
                filter: "[RevokedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_RowId",
                schema: "resources",
                table: "Agents",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentUpdateTargets_AgentId_AssignedAt",
                schema: "resources",
                table: "AgentUpdateTargets",
                columns: new[] { "AgentId", "AssignedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentUpdateTargets_PackageId",
                schema: "resources",
                table: "AgentUpdateTargets",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentUpdateTargets_RowId",
                schema: "resources",
                table: "AgentUpdateTargets",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentVersionAssignments_PackageId",
                schema: "resources",
                table: "AgentVersionAssignments",
                column: "PackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEnrollments",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "AgentUpdateTargets",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "AgentVersionAssignments",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "Agents",
                schema: "resources");
        }
    }
}

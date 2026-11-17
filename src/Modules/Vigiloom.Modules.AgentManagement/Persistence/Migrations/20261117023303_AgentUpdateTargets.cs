using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdateTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateTargets",
                schema: "agent",
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
                    table.PrimaryKey("PK_UpdateTargets", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_UpdateTargets_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_UpdateTargets_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UpdateTargets_Packages_PackageId",
                        column: x => x.PackageId,
                        principalSchema: "agent",
                        principalTable: "Packages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTargets_AgentId_AssignedAt",
                schema: "agent",
                table: "UpdateTargets",
                columns: new[] { "AgentId", "AssignedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTargets_PackageId",
                schema: "agent",
                table: "UpdateTargets",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTargets_RowId",
                schema: "agent",
                table: "UpdateTargets",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            // Preserve currently assigned updates when upgrading an existing installation.
            migrationBuilder.Sql("""
                INSERT INTO [agent].[UpdateTargets] ([Id], [AgentId], [PackageId], [Version], [RuntimeIdentifier], [ManifestSha256], [AssignedAt])
                SELECT a.[UpdateId], a.[AgentId], a.[PackageId], p.[Version], p.[RuntimeIdentifier], p.[ManifestSha256], a.[AssignedAt]
                FROM [agent].[VersionAssignments] AS a
                INNER JOIN [agent].[Packages] AS p ON p.[Id] = a.[PackageId]
                WHERE a.[UpdateId] IS NOT NULL AND a.[PackageId] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateTargets",
                schema: "agent");
        }
    }
}

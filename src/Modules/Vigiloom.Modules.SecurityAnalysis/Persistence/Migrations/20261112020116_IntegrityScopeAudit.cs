using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrityScopeAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrityScopeAudits",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityScopeAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_IntegrityScopeAudits_Action", "[Action] BETWEEN 1 AND 3 AND [Revision] > 0");
                    table.ForeignKey(
                        name: "FK_IntegrityScopeAudits_IntegrityScopes_ScopeId",
                        column: x => x.ScopeId,
                        principalSchema: "security",
                        principalTable: "IntegrityScopes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScopeAudits_DeviceId_OccurredAt_RowId",
                schema: "security",
                table: "IntegrityScopeAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScopeAudits_ScopeId",
                schema: "security",
                table: "IntegrityScopeAudits",
                column: "ScopeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntegrityScopeAudits] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot downgrade while integrity scope audit records exist.', 1;
                """);
            migrationBuilder.DropTable(
                name: "IntegrityScopeAudits",
                schema: "security");
        }
    }
}

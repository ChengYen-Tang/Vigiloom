using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentReplayPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReplayPolicies",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    GraceUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EnforcedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayPolicies", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ReplayPolicies_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_ReplayPolicies_Phase", "([Phase] = 0 AND [ActivatedAt] IS NULL AND [GraceUntil] IS NULL AND [EnforcedAt] IS NULL) OR ([Phase] = 1 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NULL) OR ([Phase] = 2 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NOT NULL AND [EnforcedAt] >= [GraceUntil])");
                    table.CheckConstraint("CK_ReplayPolicies_Scope", "[Scope] = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayPolicies_RowId",
                schema: "agent",
                table: "ReplayPolicies",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ReplayPolicies_Scope",
                schema: "agent",
                table: "ReplayPolicies",
                column: "Scope",
                unique: true);

            migrationBuilder.InsertData(
                schema: "agent", table: "ReplayPolicies",
                columns: new[] { "Id", "Scope", "Phase" },
                values: new object[] { Guid.CreateVersion7(), 1, 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK) WHERE [Phase] <> 0) THROW 51000, 'An activated replay policy cannot be downgraded.', 1;");
            migrationBuilder.DropTable(
                name: "ReplayPolicies",
                schema: "agent");
        }
    }
}

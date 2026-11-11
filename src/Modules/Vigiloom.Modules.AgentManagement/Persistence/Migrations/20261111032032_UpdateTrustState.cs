using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTrustState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateTrustStates",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Snapshot = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateTrustStates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_UpdateTrustStates_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_UpdateTrustStates_Scope", "[Scope] = 1");
                    table.CheckConstraint("CK_UpdateTrustStates_SnapshotSize", "DATALENGTH([Snapshot]) BETWEEN 1 AND 6291456");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTrustStates_RowId",
                schema: "agent",
                table: "UpdateTrustStates",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTrustStates_Scope",
                schema: "agent",
                table: "UpdateTrustStates",
                column: "Scope",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[UpdateTrustStates] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot discard provisioned update trust state.', 1;");
            migrationBuilder.DropTable(
                name: "UpdateTrustStates",
                schema: "agent");
        }
    }
}

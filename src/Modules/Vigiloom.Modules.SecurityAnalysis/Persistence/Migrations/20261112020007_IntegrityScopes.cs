using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrityScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrityScopes",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", maxLength: 32768, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityScopes", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IntegrityScopes_Definition", "ISJSON([DefinitionJson]) = 1 AND DATALENGTH([DefinitionJson]) BETWEEN 2 AND 65536");
                    table.CheckConstraint("CK_IntegrityScopes_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [UpdatedBy] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_IntegrityScopes_Revision", "[Revision] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScopes_DeviceId_RetiredAt",
                schema: "security",
                table: "IntegrityScopes",
                columns: new[] { "DeviceId", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScopes_RowId",
                schema: "security",
                table: "IntegrityScopes",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntegrityScopes] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot downgrade while integrity scopes exist.', 1;
                """);
            migrationBuilder.DropTable(
                name: "IntegrityScopes",
                schema: "security");
        }
    }
}

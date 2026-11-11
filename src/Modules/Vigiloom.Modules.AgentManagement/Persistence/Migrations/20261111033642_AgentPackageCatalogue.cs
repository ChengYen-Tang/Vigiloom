using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentPackageCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Packages",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RuntimeIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Revoked = table.Column<bool>(type: "bit", nullable: false),
                    Listed = table.Column<bool>(type: "bit", nullable: false),
                    VerifiedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Packages", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Packages_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_Packages_Size", "[Size] BETWEEN 1 AND 2147483648");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Packages_RowId",
                schema: "agent",
                table: "Packages",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Packages_Version_RuntimeIdentifier",
                schema: "agent",
                table: "Packages",
                columns: new[] { "Version", "RuntimeIdentifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[Packages] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot discard package catalogue records.', 1;");
            migrationBuilder.DropTable(
                name: "Packages",
                schema: "agent");
        }
    }
}

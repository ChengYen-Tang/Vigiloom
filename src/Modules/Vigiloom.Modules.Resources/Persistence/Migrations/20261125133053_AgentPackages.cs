using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentPackages",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RuntimeIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ManifestSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPackages", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_AgentPackages_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_AgentPackages_Size", "[Size] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPackages_RowId",
                schema: "resources",
                table: "AgentPackages",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentPackages_Version_RuntimeIdentifier",
                schema: "resources",
                table: "AgentPackages",
                columns: new[] { "Version", "RuntimeIdentifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPackages",
                schema: "resources");
        }
    }
}

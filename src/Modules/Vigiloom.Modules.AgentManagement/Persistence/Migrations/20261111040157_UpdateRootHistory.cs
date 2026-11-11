using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateRootHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateRoots",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Metadata = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRoots", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_UpdateRoots_MetadataSize", "DATALENGTH([Metadata]) BETWEEN 1 AND 1048576");
                    table.CheckConstraint("CK_UpdateRoots_Version", "[Version] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRoots_Version",
                schema: "agent",
                table: "UpdateRoots",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[UpdateRoots] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot discard trusted root history.', 1;");
            migrationBuilder.DropTable(
                name: "UpdateRoots",
                schema: "agent");
        }
    }
}

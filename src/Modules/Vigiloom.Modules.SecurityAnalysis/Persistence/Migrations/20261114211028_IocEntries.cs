using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IocEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IocEntries",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SignatureFileRowId = table.Column<long>(type: "bigint", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Reason = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IocEntries", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_IocEntries_Line", "[LineNumber] > 0");
                    table.ForeignKey(
                        name: "FK_IocEntries_SignatureFiles_SignatureFileRowId",
                        column: x => x.SignatureFileRowId,
                        principalSchema: "security",
                        principalTable: "SignatureFiles",
                        principalColumn: "RowId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IocEntries_SignatureFileRowId_LineNumber",
                schema: "security",
                table: "IocEntries",
                columns: new[] { "SignatureFileRowId", "LineNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [security].[IocEntries] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot remove retained IOC entries.', 1;");
            migrationBuilder.DropTable(
                name: "IocEntries",
                schema: "security");
        }
    }
}

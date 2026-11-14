using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignatureFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignatureFiles",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureFiles", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_SignatureFiles_Content", "DATALENGTH([Content]) BETWEEN 1 AND 1048576");
                    table.CheckConstraint("CK_SignatureFiles_Kind", "[Kind] BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_SignatureFiles_IntelligenceRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureFiles_RevisionId_Path",
                schema: "security",
                table: "SignatureFiles",
                columns: new[] { "RevisionId", "Path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [security].[SignatureFiles] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot remove retained signature files.', 1;");
            migrationBuilder.DropTable(
                name: "SignatureFiles",
                schema: "security");
        }
    }
}

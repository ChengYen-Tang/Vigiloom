using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryImportStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdvisoryImports",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BaseActivationVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    SourceVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    WindowStart = table.Column<DateOnly>(type: "date", nullable: false),
                    WindowEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    Cursor = table.Column<string>(type: "varchar(1024)", unicode: false, maxLength: 1024, nullable: true, collation: "Latin1_General_100_BIN2"),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryImports", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_AdvisoryImports_Base", "([BaseRevisionId] IS NULL AND [BaseActivationVersion] IS NULL) OR ([BaseRevisionId] IS NOT NULL AND [BaseActivationVersion] IS NOT NULL)");
                    table.CheckConstraint("CK_AdvisoryImports_Values", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [WindowStart] <= [WindowEnd] AND [Phase] BETWEEN 1 AND 5 AND [PageCount] BETWEEN 0 AND 100000 AND [RecordCount] BETWEEN 0 AND 100000 AND [TotalBytes] BETWEEN 0 AND 1073741824");
                    table.ForeignKey(
                        name: "FK_AdvisoryImports_IntelligenceRevisions_SourceId_BaseRevisionId",
                        columns: x => new { x.SourceId, x.BaseRevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                    table.ForeignKey(
                        name: "FK_AdvisoryImports_IntelligenceSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "security",
                        principalTable: "IntelligenceSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AdvisoryImportPages",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    NextCursor = table.Column<string>(type: "varchar(1024)", unicode: false, maxLength: 1024, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryImportPages", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_AdvisoryImportPages_ImportId_RowId", x => new { x.ImportId, x.RowId })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_AdvisoryImportPages_Values", "[Number] > 0 AND [Phase] BETWEEN 1 AND 2 AND DATALENGTH([Payload]) BETWEEN 1 AND 2097152");
                    table.ForeignKey(
                        name: "FK_AdvisoryImportPages_AdvisoryImports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "security",
                        principalTable: "AdvisoryImports",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AdvisoryImportEntries",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ImportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageRowId = table.Column<long>(type: "bigint", nullable: false),
                    AdvisoryId = table.Column<string>(type: "varchar(19)", unicode: false, maxLength: 19, nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryImportEntries", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_AdvisoryImportEntries_Ordinal", "[Ordinal] BETWEEN 0 AND 99");
                    table.ForeignKey(
                        name: "FK_AdvisoryImportEntries_AdvisoryImportPages_ImportId_PageRowId",
                        columns: x => new { x.ImportId, x.PageRowId },
                        principalSchema: "security",
                        principalTable: "AdvisoryImportPages",
                        principalColumns: new[] { "ImportId", "RowId" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportEntries_ImportId_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries",
                columns: new[] { "ImportId", "AdvisoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportEntries_ImportId_PageRowId",
                schema: "security",
                table: "AdvisoryImportEntries",
                columns: new[] { "ImportId", "PageRowId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportPages_ImportId_Number",
                schema: "security",
                table: "AdvisoryImportPages",
                columns: new[] { "ImportId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportPages_ImportId_Phase_NextCursor",
                schema: "security",
                table: "AdvisoryImportPages",
                columns: new[] { "ImportId", "Phase", "NextCursor" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImports_RowId",
                schema: "security",
                table: "AdvisoryImports",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImports_SourceId",
                schema: "security",
                table: "AdvisoryImports",
                column: "SourceId",
                unique: true,
                filter: "[Phase] < 4");

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImports_SourceId_BaseRevisionId",
                schema: "security",
                table: "AdvisoryImports",
                columns: new[] { "SourceId", "BaseRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImports_SourceId_StartedAt",
                schema: "security",
                table: "AdvisoryImports",
                columns: new[] { "SourceId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[AdvisoryImportPages] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[AdvisoryImportEntries] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Advisory import progress prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "AdvisoryImportEntries",
                schema: "security");

            migrationBuilder.DropTable(
                name: "AdvisoryImportPages",
                schema: "security");

            migrationBuilder.DropTable(
                name: "AdvisoryImports",
                schema: "security");
        }
    }
}

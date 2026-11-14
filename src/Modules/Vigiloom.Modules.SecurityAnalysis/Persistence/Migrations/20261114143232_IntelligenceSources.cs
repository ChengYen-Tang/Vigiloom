using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntelligenceSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntelligenceSources",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSuccessfulCheckAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntelligenceSources", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IntelligenceSources_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "IntelligenceRevisions",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpstreamVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Artifact = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MappingVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LicenseEvidence = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    Revoked = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntelligenceRevisions", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_IntelligenceRevisions_SourceId_Id", x => new { x.SourceId, x.Id })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IntelligenceRevisions_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_IntelligenceRevisions_RecordCount", "[RecordCount] >= 0");
                    table.ForeignKey(
                        name: "FK_IntelligenceRevisions_IntelligenceSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "security",
                        principalTable: "IntelligenceSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IntelligenceActivations",
                schema: "security",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntelligenceActivations", x => x.SourceId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_IntelligenceActivations_IntelligenceRevisions_SourceId_RevisionId",
                        columns: x => new { x.SourceId, x.RevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                    table.ForeignKey(
                        name: "FK_IntelligenceActivations_IntelligenceSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "security",
                        principalTable: "IntelligenceSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IntelligenceCaches",
                schema: "security",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ETag = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    LastModified = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntelligenceCaches", x => x.SourceId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_IntelligenceCaches_IntelligenceRevisions_SourceId_RevisionId",
                        columns: x => new { x.SourceId, x.RevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                    table.ForeignKey(
                        name: "FK_IntelligenceCaches_IntelligenceSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "security",
                        principalTable: "IntelligenceSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceActivations_SourceId_RevisionId",
                schema: "security",
                table: "IntelligenceActivations",
                columns: new[] { "SourceId", "RevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceCaches_SourceId_RevisionId",
                schema: "security",
                table: "IntelligenceCaches",
                columns: new[] { "SourceId", "RevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceRevisions_RowId",
                schema: "security",
                table: "IntelligenceRevisions",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceRevisions_SourceId_Digest",
                schema: "security",
                table: "IntelligenceRevisions",
                columns: new[] { "SourceId", "Digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceSources_Key",
                schema: "security",
                table: "IntelligenceSources",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceSources_RowId",
                schema: "security",
                table: "IntelligenceSources",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntelligenceSources] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[IntelligenceRevisions] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[IntelligenceActivations] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[IntelligenceCaches] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Intelligence sources and retained versions prevent downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "IntelligenceActivations",
                schema: "security");

            migrationBuilder.DropTable(
                name: "IntelligenceCaches",
                schema: "security");

            migrationBuilder.DropTable(
                name: "IntelligenceRevisions",
                schema: "security");

            migrationBuilder.DropTable(
                name: "IntelligenceSources",
                schema: "security");
        }
    }
}

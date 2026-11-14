using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntelligenceActivationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "security",
                table: "IntelligenceActivations",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "IntelligenceActivationEvents",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Scheduled = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LargeReductionConfirmed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntelligenceActivationEvents", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_IntelligenceActivationEvents_Actor", "([Scheduled] = 1 AND [ActorId] IS NULL) OR ([Scheduled] = 0 AND [ActorId] IS NOT NULL AND [ActorId] <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "FK_IntelligenceActivationEvents_IntelligenceRevisions_SourceId_PreviousRevisionId",
                        columns: x => new { x.SourceId, x.PreviousRevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                    table.ForeignKey(
                        name: "FK_IntelligenceActivationEvents_IntelligenceRevisions_SourceId_RevisionId",
                        columns: x => new { x.SourceId, x.RevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceActivationEvents_SourceId_OccurredAt_RowId",
                schema: "security",
                table: "IntelligenceActivationEvents",
                columns: new[] { "SourceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceActivationEvents_SourceId_PreviousRevisionId",
                schema: "security",
                table: "IntelligenceActivationEvents",
                columns: new[] { "SourceId", "PreviousRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceActivationEvents_SourceId_RevisionId",
                schema: "security",
                table: "IntelligenceActivationEvents",
                columns: new[] { "SourceId", "RevisionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntelligenceActivationEvents] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Intelligence activation history prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "IntelligenceActivationEvents",
                schema: "security");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "security",
                table: "IntelligenceActivations");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryCursorRestart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RestartCount",
                schema: "security",
                table: "AdvisoryImports",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SuccessorId",
                schema: "security",
                table: "AdvisoryImports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvisoryImports_RestartCount",
                schema: "security",
                table: "AdvisoryImports",
                sql: "[RestartCount] BETWEEN 0 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [RestartCount] > 0 OR [SuccessorId] IS NOT NULL)
                    THROW 51000, 'Advisory restart history prevents downgrade.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvisoryImports_RestartCount",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "RestartCount",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "SuccessorId",
                schema: "security",
                table: "AdvisoryImports");
        }
    }
}

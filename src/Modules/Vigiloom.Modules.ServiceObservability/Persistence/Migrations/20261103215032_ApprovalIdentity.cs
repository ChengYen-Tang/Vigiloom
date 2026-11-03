using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CandidateId",
                schema: "service",
                table: "Services",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Services_CandidateId",
                schema: "service",
                table: "Services",
                column: "CandidateId",
                unique: true,
                filter: "[CandidateId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Services_CandidateId",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "CandidateId",
                schema: "service",
                table: "Services");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.HostMigrations
{
    /// <inheritdoc />
    public partial class HostUpdateReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateReceipts",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetryAllowed = table.Column<bool>(type: "bit", nullable: false),
                    Failure = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateReceipts", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_UpdateReceipts_Result", "[Sequence] > 0 AND [Result] IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10) AND ([Failure] IS NULL OR [Failure] IN (1, 2, 3)) AND ([Result] IN (2, 3, 10) OR ([RetryAllowed] = 0 AND [Failure] IS NULL))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateReceipts_AgentId_MessageId",
                schema: "agent",
                table: "UpdateReceipts",
                columns: new[] { "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateReceipts_AgentId_ReceivedAt_RowId",
                schema: "agent",
                table: "UpdateReceipts",
                columns: new[] { "AgentId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateReceipts_UpdateId_Sequence",
                schema: "agent",
                table: "UpdateReceipts",
                columns: new[] { "UpdateId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateReceipts",
                schema: "agent");
        }
    }
}

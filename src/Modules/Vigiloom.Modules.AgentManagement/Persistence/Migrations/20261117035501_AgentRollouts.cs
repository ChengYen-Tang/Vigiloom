using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentRollouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Rollouts",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PackageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PilotSize = table.Column<int>(type: "int", nullable: false),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rollouts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Rollouts_BatchSize", "[PilotSize] BETWEEN 1 AND 100 AND [BatchSize] BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_Rollouts_Status", "[Status] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_Rollouts_Packages_PackageId",
                        column: x => x.PackageId,
                        principalSchema: "agent",
                        principalTable: "Packages",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RolloutAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RolloutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_RolloutAudits_Rollouts_RolloutId",
                        column: x => x.RolloutId,
                        principalSchema: "agent",
                        principalTable: "Rollouts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RolloutMembers",
                schema: "agent",
                columns: table => new
                {
                    RolloutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Batch = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    ExpectedAssignmentVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    UpdateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Problem = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolloutMembers", x => new { x.RolloutId, x.AgentId })
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_RolloutMembers_Order", "[Batch] >= 0 AND [Position] >= 0");
                    table.CheckConstraint("CK_RolloutMembers_Status", "[Status] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_RolloutMembers_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RolloutMembers_Rollouts_RolloutId",
                        column: x => x.RolloutId,
                        principalSchema: "agent",
                        principalTable: "Rollouts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RolloutAudits_RolloutId_OccurredAt_RowId",
                schema: "agent",
                table: "RolloutAudits",
                columns: new[] { "RolloutId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_RolloutMembers_AgentId",
                schema: "agent",
                table: "RolloutMembers",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_RolloutMembers_RolloutId_Batch_Position",
                schema: "agent",
                table: "RolloutMembers",
                columns: new[] { "RolloutId", "Batch", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rollouts_PackageId",
                schema: "agent",
                table: "Rollouts",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_Rollouts_RowId",
                schema: "agent",
                table: "Rollouts",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Rollouts_Status_RowId",
                schema: "agent",
                table: "Rollouts",
                columns: new[] { "Status", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolloutAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "RolloutMembers",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "Rollouts",
                schema: "agent");
        }
    }
}

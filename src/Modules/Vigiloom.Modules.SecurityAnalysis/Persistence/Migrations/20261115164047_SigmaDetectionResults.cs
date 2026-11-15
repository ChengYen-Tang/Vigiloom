using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SigmaDetectionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigmaCorrelationEvents",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivationVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaCorrelationEvents", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_SigmaCorrelationEvents_DeviceGeneration", "[DeviceGeneration] >= 0");
                    table.ForeignKey(
                        name: "FK_SigmaCorrelationEvents_SigmaRuleTests_TestId",
                        column: x => x.TestId,
                        principalSchema: "security",
                        principalTable: "SigmaRuleTests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SigmaDetectionResults",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivationVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    Result = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaDetectionResults", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_SigmaDetectionResults_Result", "[Result] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_SigmaDetectionResults_SigmaRuleActivations_ActivationId",
                        column: x => x.ActivationId,
                        principalSchema: "security",
                        principalTable: "SigmaRuleActivations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SigmaDetectionResults_SigmaRuleTests_TestId",
                        column: x => x.TestId,
                        principalSchema: "security",
                        principalTable: "SigmaRuleTests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SigmaEventGaps",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaEventGaps", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_SigmaEventGaps_Kind", "[Kind] IN (1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaCorrelationEvents_TestId_ActivationVersion_AgentId_MessageId",
                schema: "security",
                table: "SigmaCorrelationEvents",
                columns: new[] { "TestId", "ActivationVersion", "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaCorrelationEvents_TestId_ActivationVersion_OccurredAt_RowId",
                schema: "security",
                table: "SigmaCorrelationEvents",
                columns: new[] { "TestId", "ActivationVersion", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaDetectionResults_ActivationId",
                schema: "security",
                table: "SigmaDetectionResults",
                column: "ActivationId");

            migrationBuilder.CreateIndex(
                name: "IX_SigmaDetectionResults_AgentId_MessageId_TestId_ActivationVersion",
                schema: "security",
                table: "SigmaDetectionResults",
                columns: new[] { "AgentId", "MessageId", "TestId", "ActivationVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaDetectionResults_DeviceId_ReceivedAt_RowId",
                schema: "security",
                table: "SigmaDetectionResults",
                columns: new[] { "DeviceId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaDetectionResults_TestId",
                schema: "security",
                table: "SigmaDetectionResults",
                column: "TestId");

            migrationBuilder.CreateIndex(
                name: "IX_SigmaEventGaps_AgentId_MessageId_Kind",
                schema: "security",
                table: "SigmaEventGaps",
                columns: new[] { "AgentId", "MessageId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaEventGaps_DeviceId_RecordedAt_RowId",
                schema: "security",
                table: "SigmaEventGaps",
                columns: new[] { "DeviceId", "RecordedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[SigmaCorrelationEvents] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [security].[SigmaDetectionResults] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [security].[SigmaEventGaps] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Sigma detection data prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "SigmaCorrelationEvents",
                schema: "security");

            migrationBuilder.DropTable(
                name: "SigmaDetectionResults",
                schema: "security");

            migrationBuilder.DropTable(
                name: "SigmaEventGaps",
                schema: "security");
        }
    }
}

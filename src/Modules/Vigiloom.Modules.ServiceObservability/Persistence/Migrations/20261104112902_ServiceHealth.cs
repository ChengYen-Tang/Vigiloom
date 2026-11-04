using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServiceHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthNetworkPolicies",
                schema: "service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NetworksJson = table.Column<string>(type: "nvarchar(max)", maxLength: 4608, nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthNetworkPolicies", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_HealthNetworkPolicies_Central", "[Name] = 'central'");
                    table.CheckConstraint("CK_HealthNetworkPolicies_Generation", "[Generation] > 0");
                });

            migrationBuilder.CreateTable(
                name: "HealthTargets",
                schema: "service",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    FailureThreshold = table.Column<int>(type: "int", nullable: false),
                    RecoveryThreshold = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthTargets", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_HealthTargets_Settings", "[IntervalSeconds] BETWEEN 5 AND 3600 AND [TimeoutSeconds] BETWEEN 1 AND 60 AND [TimeoutSeconds] <= [IntervalSeconds] AND [FailureThreshold] BETWEEN 1 AND 10 AND [RecoveryThreshold] BETWEEN 1 AND 10");
                    table.ForeignKey(
                        name: "FK_HealthTargets_Instances_InstanceId_ServiceId",
                        columns: x => new { x.InstanceId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "Instances",
                        principalColumns: new[] { "Id", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HealthTargets_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HealthNetworkRevisions",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    NetworksJson = table.Column<string>(type: "nvarchar(max)", maxLength: 4608, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthNetworkRevisions", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_HealthNetworkRevisions_HealthNetworkPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "service",
                        principalTable: "HealthNetworkPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HealthObservations",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    NetworkGeneration = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    ServiceVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    InstanceVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    TargetVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    NetworkVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthObservations", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_HealthObservations_Values", "[LeaseGeneration] > 0 AND [NetworkGeneration] > 0 AND [Kind] BETWEEN 1 AND 7");
                    table.ForeignKey(
                        name: "FK_HealthObservations_HealthTargets_TargetId",
                        column: x => x.TargetId,
                        principalSchema: "service",
                        principalTable: "HealthTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HealthSchedules",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    EvidenceAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Streak = table.Column<byte>(type: "tinyint", nullable: false),
                    StreakCount = table.Column<int>(type: "int", nullable: false),
                    StreakAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SourceVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    ServiceVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    InstanceVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    TargetVersion = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    NetworkVersion = table.Column<byte[]>(type: "binary(8)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthSchedules", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_HealthSchedules_Generation", "[Generation] >= 0");
                    table.CheckConstraint("CK_HealthSchedules_State", "[State] BETWEEN 0 AND 3 AND [Streak] BETWEEN 0 AND 3 AND [StreakCount] BETWEEN 0 AND 10 AND (([State] = 0 AND [EvidenceAt] IS NULL) OR ([State] > 0 AND [EvidenceAt] IS NOT NULL)) AND (([Streak] = 0 AND [StreakCount] = 0 AND [StreakAt] IS NULL) OR ([Streak] > 0 AND [StreakCount] > 0 AND [StreakAt] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_HealthSchedules_HealthTargets_TargetId",
                        column: x => x.TargetId,
                        principalSchema: "service",
                        principalTable: "HealthTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HealthStateIntervals",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    Reason = table.Column<byte>(type: "tinyint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthStateIntervals", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_HealthStateIntervals_Values", "[State] BETWEEN 0 AND 4 AND [Reason] BETWEEN 0 AND 9 AND ([EndedAt] IS NULL OR [EndedAt] >= [StartedAt])");
                    table.ForeignKey(
                        name: "FK_HealthStateIntervals_HealthTargets_TargetId",
                        column: x => x.TargetId,
                        principalSchema: "service",
                        principalTable: "HealthTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthNetworkPolicies_Name",
                schema: "service",
                table: "HealthNetworkPolicies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthNetworkPolicies_RowId",
                schema: "service",
                table: "HealthNetworkPolicies",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthNetworkRevisions_PolicyId_Generation",
                schema: "service",
                table: "HealthNetworkRevisions",
                columns: new[] { "PolicyId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthObservations_TargetId_LeaseGeneration",
                schema: "service",
                table: "HealthObservations",
                columns: new[] { "TargetId", "LeaseGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthObservations_TargetId_ObservedAt_RowId",
                schema: "service",
                table: "HealthObservations",
                columns: new[] { "TargetId", "ObservedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthSchedules_Enabled_DueAt_RowId",
                schema: "service",
                table: "HealthSchedules",
                columns: new[] { "Enabled", "DueAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthSchedules_TargetId",
                schema: "service",
                table: "HealthSchedules",
                column: "TargetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthStateIntervals_TargetId",
                schema: "service",
                table: "HealthStateIntervals",
                column: "TargetId",
                unique: true,
                filter: "[EndedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HealthStateIntervals_TargetId_StartedAt_RowId",
                schema: "service",
                table: "HealthStateIntervals",
                columns: new[] { "TargetId", "StartedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthTargets_InstanceId_ServiceId",
                schema: "service",
                table: "HealthTargets",
                columns: new[] { "InstanceId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthTargets_RowId",
                schema: "service",
                table: "HealthTargets",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthTargets_ServiceId_InstanceId",
                schema: "service",
                table: "HealthTargets",
                columns: new[] { "ServiceId", "InstanceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthNetworkRevisions",
                schema: "service");

            migrationBuilder.DropTable(
                name: "HealthObservations",
                schema: "service");

            migrationBuilder.DropTable(
                name: "HealthSchedules",
                schema: "service");

            migrationBuilder.DropTable(
                name: "HealthStateIntervals",
                schema: "service");

            migrationBuilder.DropTable(
                name: "HealthNetworkPolicies",
                schema: "service");

            migrationBuilder.DropTable(
                name: "HealthTargets",
                schema: "service");
        }
    }
}

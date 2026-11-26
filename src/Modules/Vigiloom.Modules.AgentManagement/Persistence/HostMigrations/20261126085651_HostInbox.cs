using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.HostMigrations
{
    /// <inheritdoc />
    public partial class HostInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostInboxStates",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ReplayClockFloor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReplayCleanedBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatchReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatchEnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatchReceiptRowId = table.Column<long>(type: "bigint", nullable: true),
                    InboxDeletedTotal = table.Column<long>(type: "bigint", nullable: false),
                    InboxLastDeletedCount = table.Column<int>(type: "int", nullable: false),
                    InboxLastDeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InboxReceiptCount = table.Column<long>(type: "bigint", nullable: true),
                    InboxReceiptPayloadBytes = table.Column<long>(type: "bigint", nullable: true),
                    LastReplayIssue = table.Column<int>(type: "int", nullable: true),
                    LastReplayIssueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostInboxStates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "ReplayPolicies",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    FreshInstallation = table.Column<bool>(type: "bit", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    GraceUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EnforcedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CleanupCutoverAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LegacyReceiptsRetainUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReplayWindowSeconds = table.Column<int>(type: "int", nullable: false, defaultValue: 86400),
                    ReceiptRetentionDays = table.Column<int>(type: "int", nullable: false, defaultValue: 7),
                    InboxSettingsChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayPolicies", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ReplayPolicies_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_ReplayPolicies_InboxTimes", "([CleanupCutoverAt] IS NULL AND [InboxSettingsChangedAt] IS NULL) OR ([Phase] = 2 AND [CleanupCutoverAt] IS NOT NULL AND [InboxSettingsChangedAt] IS NOT NULL AND [InboxSettingsChangedAt] >= [CleanupCutoverAt] AND [CleanupCutoverAt] >= [EnforcedAt])");
                    table.CheckConstraint("CK_ReplayPolicies_LegacyDeadline", "([CleanupCutoverAt] IS NULL AND [LegacyReceiptsRetainUntil] IS NULL) OR ([CleanupCutoverAt] IS NOT NULL AND [LegacyReceiptsRetainUntil] IS NOT NULL AND [LegacyReceiptsRetainUntil] >= [CleanupCutoverAt])");
                    table.CheckConstraint("CK_ReplayPolicies_Phase", "([Phase] = 0 AND [FreshInstallation] = 0 AND [ActivatedAt] IS NULL AND [GraceUntil] IS NULL AND [EnforcedAt] IS NULL) OR ([Phase] = 1 AND [FreshInstallation] = 0 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NULL) OR ([Phase] = 2 AND [ActivatedAt] IS NOT NULL AND [EnforcedAt] IS NOT NULL AND (([FreshInstallation] = 1 AND [GraceUntil] IS NULL AND [EnforcedAt] = [ActivatedAt]) OR ([FreshInstallation] = 0 AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] >= [GraceUntil])))");
                    table.CheckConstraint("CK_ReplayPolicies_ReceiptRetentionDays", "[ReceiptRetentionDays] BETWEEN 1 AND 3650");
                    table.CheckConstraint("CK_ReplayPolicies_ReplayWindowSeconds", "[ReplayWindowSeconds] BETWEEN 1 AND 2592000");
                    table.CheckConstraint("CK_ReplayPolicies_Scope", "[Scope] = 1");
                    table.CheckConstraint("CK_ReplayPolicies_WindowWithinRetention", "CAST([ReceiptRetentionDays] AS bigint) * 86400 >= CAST([ReplayWindowSeconds] AS bigint) + 300");
                });

            migrationBuilder.CreateTable(
                name: "ReplayGenerations",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WindowSeconds = table.Column<int>(type: "int", nullable: false),
                    ValidThrough = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayGenerations", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_ReplayGenerations_WindowSeconds", "[WindowSeconds] BETWEEN 1 AND 2592000");
                    table.ForeignKey(
                        name: "FK_ReplayGenerations_ReplayPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "agent",
                        principalTable: "ReplayPolicies",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostInboxStates_RowId",
                schema: "agent",
                table: "HostInboxStates",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ReplayGenerations_PolicyId_ValidThrough",
                schema: "agent",
                table: "ReplayGenerations",
                columns: new[] { "PolicyId", "ValidThrough" })
                .Annotation("SqlServer:Include", new[] { "WindowSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayPolicies_RowId",
                schema: "agent",
                table: "ReplayPolicies",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ReplayPolicies_Scope",
                schema: "agent",
                table: "ReplayPolicies",
                column: "Scope",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostInboxStates",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "ReplayGenerations",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "ReplayPolicies",
                schema: "agent");
        }
    }
}

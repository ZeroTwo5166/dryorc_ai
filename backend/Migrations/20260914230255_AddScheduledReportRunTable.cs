using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledReportRunTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledReportRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportPeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledReportRuns", x => x.Id);
                    table.CheckConstraint("CK_ScheduledReportRuns_SentAt", "(\"Status\" = 'Sent' AND \"SentAt\" IS NOT NULL)\r\nOR\r\n(\"Status\" <> 'Sent' AND \"SentAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_ScheduledReportRuns_AspNetUsers_RecipientOwnerId",
                        column: x => x.RecipientOwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScheduledReportRuns_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReportRuns_BusinessId_Processing",
                table: "ScheduledReportRuns",
                column: "BusinessId",
                filter: "\"Status\" = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReportRuns_BusinessId_ReportPeriodStart",
                table: "ScheduledReportRuns",
                columns: new[] { "BusinessId", "ReportPeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReportRuns_RecipientOwnerId",
                table: "ScheduledReportRuns",
                column: "RecipientOwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledReportRuns");
        }
    }
}

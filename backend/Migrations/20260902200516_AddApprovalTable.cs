using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CanonicalArguments = table.Column<string>(type: "text", nullable: false),
                    ActionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplaySummary = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DeniedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeniedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OrderCancellationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Approvals_AspNetUsers_ApprovedBy",
                        column: x => x.ApprovedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Approvals_AspNetUsers_DeniedBy",
                        column: x => x.DeniedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Approvals_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Approvals_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Approvals_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Approvals_OrderCancellations_OrderCancellationId",
                        column: x => x.OrderCancellationId,
                        principalTable: "OrderCancellations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_ApprovedBy",
                table: "Approvals",
                column: "ApprovedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_BusinessId",
                table: "Approvals",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_ConversationId",
                table: "Approvals",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_CustomerId",
                table: "Approvals",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_DeniedBy",
                table: "Approvals",
                column: "DeniedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_OrderCancellationId",
                table: "Approvals",
                column: "OrderCancellationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Approvals");
        }
    }
}

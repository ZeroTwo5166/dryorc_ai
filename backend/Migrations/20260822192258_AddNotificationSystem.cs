using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.CheckConstraint("CK_Notifications_Actor", "(\"ActorType\" = 'NivoraBusinessStaff' AND \"ActorUserId\" IS NOT NULL)\r\nOR\r\n(\"ActorType\" <> 'NivoraBusinessStaff' AND \"ActorUserId\" IS NULL)");
                    table.CheckConstraint("CK_Notifications_CategoryRefs", "\"Category\" <> 'Team'\r\nOR\r\n(\"ConversationId\" IS NULL AND \"TicketId\" IS NULL)");
                    table.CheckConstraint("CK_Notifications_CategoryTypeConsistency", "(\"Category\" = 'Team' AND \"Type\" IN ('TeamMemberJoined', 'StaffRoleChanged', 'StaffAccessRevoked'))\r\nOR\r\n(\"Category\" = 'Customer' AND \"Type\" IN ('ConversationAssigned', 'TicketSlaWarning', 'CustomerReplied', 'NewConversation', 'ConversationEscalated'))\r\nOR\r\n(\"Category\" = 'System' AND \"Type\" IN ('KnowledgeImportCompleted', 'IntegrationSyncCompleted'))");
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notifications_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NotificationRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationRecipients_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationRecipients_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_BusinessId_UserId",
                table: "NotificationRecipients",
                columns: new[] { "BusinessId", "UserId" },
                filter: "\"IsRead\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_BusinessId_UserId_CreatedAt_Id",
                table: "NotificationRecipients",
                columns: new[] { "BusinessId", "UserId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_NotificationId_UserId",
                table: "NotificationRecipients",
                columns: new[] { "NotificationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecipients_UserId",
                table: "NotificationRecipients",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ActorUserId",
                table: "Notifications",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_BusinessId_Category_CreatedAt",
                table: "Notifications",
                columns: new[] { "BusinessId", "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_BusinessId_ConversationId",
                table: "Notifications",
                columns: new[] { "BusinessId", "ConversationId" },
                filter: "\"ConversationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_BusinessId_IdempotencyKey",
                table: "Notifications",
                columns: new[] { "BusinessId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ConversationId",
                table: "Notifications",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TicketId",
                table: "Notifications",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationRecipients");

            migrationBuilder.DropTable(
                name: "Notifications");
        }
    }
}

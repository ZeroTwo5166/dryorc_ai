using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffInvitationTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffInvitations", x => x.Id);
                    table.CheckConstraint("CK_StaffInvitations_ExpiresAfterCreated", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_StaffInvitations_LastSentAt_Range", "\"LastSentAt\" >= \"CreatedAt\"");
                    table.CheckConstraint("CK_StaffInvitations_Role", "\"Role\" IN ('Admin', 'Agent')");
                    table.CheckConstraint("CK_StaffInvitations_TokenHash_Length", "octet_length(\"TokenHash\") = 32");
                    table.ForeignKey(
                        name: "FK_StaffInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffInvitations_Businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "Businesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffInvitations_BusinessId_NormalizedKey",
                table: "StaffInvitations",
                columns: new[] { "BusinessId", "NormalizedKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffInvitations_ExpiresAt",
                table: "StaffInvitations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_StaffInvitations_InvitedByUserId",
                table: "StaffInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffInvitations_TokenHash",
                table: "StaffInvitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffInvitations");
        }
    }
}

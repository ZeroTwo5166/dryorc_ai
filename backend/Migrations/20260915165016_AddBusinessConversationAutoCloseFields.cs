using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessConversationAutoCloseFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConversationAutoCloseEnabled",
                table: "Businesses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ConversationInactivityCloseDays",
                table: "Businesses",
                type: "integer",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Business_ConversationInactivityCloseDays_Range",
                table: "Businesses",
                sql: "\"ConversationInactivityCloseDays\" BETWEEN 1 AND 365");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Business_ConversationInactivityCloseDays_Range",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "ConversationAutoCloseEnabled",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "ConversationInactivityCloseDays",
                table: "Businesses");
        }
    }
}

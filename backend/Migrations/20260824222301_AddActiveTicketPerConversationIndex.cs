using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveTicketPerConversationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_ConversationId",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ActiveTicketPerConversation",
                table: "Tickets",
                column: "ConversationId",
                unique: true,
                filter: "\"ConversationId\" IS NOT NULL AND \"Status\" <> 'Resolved'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_ActiveTicketPerConversation",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ConversationId",
                table: "Tickets",
                column: "ConversationId");
        }
    }
}

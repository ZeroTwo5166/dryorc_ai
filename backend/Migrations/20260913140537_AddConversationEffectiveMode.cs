using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationEffectiveMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited from the scaffolded version: the tool's own
            // generated defaultValue was "" (empty string), not a real
            // ConversationMode member — matching HasConversion<string>()'s
            // bare-enum-member-name storage convention (same precedent as
            // AddOrderCancellationReasonAndRefundMethod's "Customer"
            // default), every pre-existing Conversation row backfills to
            // "Auto", the enum's own weakest value — a deliberate
            // "start most-permissive" choice, per step-22 design doc §C.
            migrationBuilder.AddColumn<string>(
                name: "EffectiveMode",
                table: "Conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Auto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveMode",
                table: "Conversations");
        }
    }
}

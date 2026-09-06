using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCancellationPendingGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OrderCancellations_BusinessId_ExternalOrderId_Pending",
                table: "OrderCancellations",
                columns: new[] { "BusinessId", "ExternalOrderId" },
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderCancellations_BusinessId_ExternalOrderId_Pending",
                table: "OrderCancellations");
        }
    }
}

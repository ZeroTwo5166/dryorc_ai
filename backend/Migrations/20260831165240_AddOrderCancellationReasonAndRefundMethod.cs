using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCancellationReasonAndRefundMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited from the scaffolded version: the tool's own
            // generated Up() dropped "Refund" before adding "Method",
            // losing data. Add both new columns first, backfill "Method"
            // from the existing "Refund" boolean, then drop "Refund" —
            // true -> OriginalPaymentMethod, false -> null (no refund),
            // matching RefundMethod's HasConversion<string>() storage
            // (bare enum member names, not Shopify's own UPPERCASE wire
            // strings — that mapping lives only in
            // ShopifyOrderCancellationExecutor).
            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "OrderCancellations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Default matches OrderCancellation.Reason's C# default
            // (OrderCancelReason.Customer) — every pre-existing row was
            // created before this column existed, i.e. by
            // OrderCancellationServiceTests only (nothing production-facing
            // calls RequestCancellationAsync yet), so this default is a
            // placeholder for test rows, never a real customer-facing
            // cancellation's actual reason.
            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "OrderCancellations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Customer");

            migrationBuilder.Sql(
                """
                UPDATE "OrderCancellations"
                SET "Method" = CASE WHEN "Refund" THEN 'OriginalPaymentMethod' ELSE NULL END;
                """);

            migrationBuilder.DropColumn(
                name: "Refund",
                table: "OrderCancellations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Refund",
                table: "OrderCancellations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE "OrderCancellations"
                SET "Refund" = ("Method" IS NOT NULL);
                """);

            migrationBuilder.DropColumn(
                name: "Method",
                table: "OrderCancellations");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "OrderCancellations");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalRefundId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RefundId",
                table: "Approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_RefundId",
                table: "Approvals",
                column: "RefundId");

            migrationBuilder.AddForeignKey(
                name: "FK_Approvals_Refunds_RefundId",
                table: "Approvals",
                column: "RefundId",
                principalTable: "Refunds",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Approvals_Refunds_RefundId",
                table: "Approvals");

            migrationBuilder.DropIndex(
                name: "IX_Approvals_RefundId",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "RefundId",
                table: "Approvals");
        }
    }
}

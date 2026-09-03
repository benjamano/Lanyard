using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnlineOrderPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PaidDate",
                table: "KitchenOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentIntentId",
                table: "KitchenOrders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedDate",
                table: "KitchenOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeAccountId",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_KitchenOrders_PaymentIntentId",
                table: "KitchenOrders",
                column: "PaymentIntentId",
                unique: true,
                filter: "\"PaymentIntentId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KitchenOrders_PaymentIntentId",
                table: "KitchenOrders");

            migrationBuilder.DropColumn(
                name: "PaidDate",
                table: "KitchenOrders");

            migrationBuilder.DropColumn(
                name: "PaymentIntentId",
                table: "KitchenOrders");

            migrationBuilder.DropColumn(
                name: "RefundedDate",
                table: "KitchenOrders");

            migrationBuilder.DropColumn(
                name: "StripeAccountId",
                table: "Companies");
        }
    }
}

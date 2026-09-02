using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssignKiosksToVenuesAndClaimPrintedReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReceiptPrintedDate",
                table: "KitchenOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "Clients",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_LocationId",
                table: "Clients",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Locations_LocationId",
                table: "Clients",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Any venue that already names a receipt printer has, by saying so, told us where
            // that kiosk is. Backfilling from it keeps existing printer configuration working:
            // without this every already-configured venue would fail the new "is this kiosk
            // yours" check the next time anyone saved that screen.
            migrationBuilder.Sql("""
                UPDATE "Clients" AS c
                SET "LocationId" = l."Id"
                FROM "Locations" AS l
                WHERE l."ReceiptPrinterClientId" = c."Id"
                  AND c."LocationId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Locations_LocationId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_LocationId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ReceiptPrintedDate",
                table: "KitchenOrders");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Clients");
        }
    }
}

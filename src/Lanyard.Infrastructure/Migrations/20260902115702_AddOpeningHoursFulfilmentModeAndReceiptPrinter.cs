using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpeningHoursFulfilmentModeAndReceiptPrinter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FulfilmentMode",
                table: "Locations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ReceiptPrinterClientId",
                table: "Locations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Locations",
                type: "text",
                nullable: false,
                // Scaffolded as "" for a non-nullable string, which reads as "no zone" and makes
                // every existing venue evaluate its opening hours in UTC - an hour out for half
                // the year, in the direction of staying open. Defaulted and backfilled instead.
                defaultValue: "Europe/London");

            migrationBuilder.Sql(@"
                UPDATE ""Locations"" SET ""TimeZoneId"" = 'Europe/London'
                WHERE COALESCE(""TimeZoneId"", '') = '';
            ");

            migrationBuilder.CreateTable(
                name: "LocationOpeningHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    OpensAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    ClosesAt = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationOpeningHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocationOpeningHours_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ReceiptPrinterClientId",
                table: "Locations",
                column: "ReceiptPrinterClientId");

            migrationBuilder.CreateIndex(
                name: "IX_LocationOpeningHours_LocationId_DayOfWeek",
                table: "LocationOpeningHours",
                columns: new[] { "LocationId", "DayOfWeek" });

            migrationBuilder.AddForeignKey(
                name: "FK_Locations_Clients_ReceiptPrinterClientId",
                table: "Locations",
                column: "ReceiptPrinterClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Locations_Clients_ReceiptPrinterClientId",
                table: "Locations");

            migrationBuilder.DropTable(
                name: "LocationOpeningHours");

            migrationBuilder.DropIndex(
                name: "IX_Locations_ReceiptPrinterClientId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "FulfilmentMode",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ReceiptPrinterClientId",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "Locations");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartyBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EatTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LaserTagTimeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PartyType = table.Column<string>(type: "text", nullable: false),
                    ChildName = table.Column<string>(type: "text", nullable: false),
                    ChildAgeTurning = table.Column<int>(type: "integer", nullable: true),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    ContactPhone = table.Column<string>(type: "text", nullable: false),
                    ContactEmail = table.Column<string>(type: "text", nullable: true),
                    ExpectedChildren = table.Column<int>(type: "integer", nullable: false),
                    ExpectedAdults = table.Column<int>(type: "integer", nullable: true),
                    Room = table.Column<string>(type: "text", nullable: true),
                    MenuType = table.Column<int>(type: "integer", nullable: false),
                    PartyHostUserId = table.Column<string>(type: "text", nullable: true),
                    AllergyNotes = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    TotalPrice = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    DepositAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    DepositPaidUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BalancePaidUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaymentNotes = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartyBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartyBookings_AspNetUsers_PartyHostUserId",
                        column: x => x.PartyHostUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PartyBookings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartyBookings_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartyBookings_CompanyId",
                table: "PartyBookings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_PartyBookings_LocationId_StartUtc",
                table: "PartyBookings",
                columns: new[] { "LocationId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PartyBookings_PartyHostUserId",
                table: "PartyBookings",
                column: "PartyHostUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartyBookings");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExternalIsProvisional",
                table: "PartyBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalLastImportedUtc",
                table: "PartyBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "PartyBookings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "PartyBookings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingImportRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    BookingsSeen = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingImportRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingImportRuns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingImportRuns_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BookingImportSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordProtected = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingImportSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingImportSettings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingImportSettings_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartyBookings_LocationId_ExternalSource_ExternalRef",
                table: "PartyBookings",
                columns: new[] { "LocationId", "ExternalSource", "ExternalRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingImportRuns_CompanyId",
                table: "BookingImportRuns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingImportRuns_LocationId_StartedAtUtc",
                table: "BookingImportRuns",
                columns: new[] { "LocationId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingImportRuns_StartedAtUtc",
                table: "BookingImportRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BookingImportSettings_CompanyId",
                table: "BookingImportSettings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingImportSettings_LocationId",
                table: "BookingImportSettings",
                column: "LocationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingImportRuns");

            migrationBuilder.DropTable(
                name: "BookingImportSettings");

            migrationBuilder.DropIndex(
                name: "IX_PartyBookings_LocationId_ExternalSource_ExternalRef",
                table: "PartyBookings");

            migrationBuilder.DropColumn(
                name: "ExternalIsProvisional",
                table: "PartyBookings");

            migrationBuilder.DropColumn(
                name: "ExternalLastImportedUtc",
                table: "PartyBookings");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "PartyBookings");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "PartyBookings");
        }
    }
}

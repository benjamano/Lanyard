using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateSuggestionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TemplateSuggestionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    RunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    Suggestions = table.Column<int>(type: "integer", nullable: false),
                    DismissedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DismissedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateSuggestionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateSuggestionRuns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemplateSuggestionRuns_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateSuggestionRuns_CompanyId",
                table: "TemplateSuggestionRuns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateSuggestionRuns_LocationId_RunUtc",
                table: "TemplateSuggestionRuns",
                columns: new[] { "LocationId", "RunUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TemplateSuggestionRuns");
        }
    }
}

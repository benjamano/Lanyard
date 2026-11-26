using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationFeatureSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocationFeatureSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Feature = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocationFeatureSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocationFeatureSettings_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocationFeatureSettings_LocationId_Feature",
                table: "LocationFeatureSettings",
                columns: new[] { "LocationId", "Feature" },
                unique: true);

            // Switches move from the company to each of its locations, and no row now means off.
            // Every location gets every feature its company had (no company row, or an enabled
            // one), so nothing changes for anyone. The list is the CompanyFeature members that
            // exist today; a member added later starts off everywhere, as intended.
            migrationBuilder.Sql("""
                INSERT INTO "LocationFeatureSettings" ("LocationId", "Feature", "IsEnabled", "UpdateDate", "UpdatedByUserId")
                SELECT l."Id", f."Feature", TRUE, now() AT TIME ZONE 'UTC', NULL
                FROM "Locations" l
                CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8), (10), (11)) AS f("Feature")
                WHERE NOT EXISTS (
                    SELECT 1 FROM "CompanyFeatureSettings" c
                    WHERE c."CompanyId" = l."CompanyId" AND c."Feature" = f."Feature" AND NOT c."IsEnabled");
                """);

            migrationBuilder.DropTable(
                name: "CompanyFeatureSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyFeatureSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Feature = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyFeatureSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CompanyFeatureSettings_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyFeatureSettings_CompanyId_Feature",
                table: "CompanyFeatureSettings",
                columns: new[] { "CompanyId", "Feature" },
                unique: true);

            // Back to per company, where no row meant on: a company keeps a feature if any of its
            // active locations has it, and gets a switched-off row otherwise.
            migrationBuilder.Sql("""
                INSERT INTO "CompanyFeatureSettings" ("CompanyId", "Feature", "IsEnabled", "UpdateDate", "UpdatedByUserId")
                SELECT co."Id", f."Feature", FALSE, now() AT TIME ZONE 'UTC', NULL
                FROM "Companies" co
                CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8), (10), (11)) AS f("Feature")
                WHERE NOT EXISTS (
                    SELECT 1 FROM "LocationFeatureSettings" s
                    JOIN "Locations" l ON l."Id" = s."LocationId"
                    WHERE l."CompanyId" = co."Id" AND l."IsActive" AND s."Feature" = f."Feature" AND s."IsEnabled");
                """);

            migrationBuilder.DropTable(
                name: "LocationFeatureSettings");
        }
    }
}

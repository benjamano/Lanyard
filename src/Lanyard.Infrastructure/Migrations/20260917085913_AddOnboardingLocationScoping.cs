using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingLocationScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId",
                table: "CompanyOnboardingSettings");

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "CompanyOnboardingStandingAttachments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "CompanyOnboardingSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingStandingAttachments_LocationId",
                table: "CompanyOnboardingStandingAttachments",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId",
                table: "CompanyOnboardingSettings",
                column: "CompanyId",
                unique: true,
                filter: "\"LocationId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId_LocationId",
                table: "CompanyOnboardingSettings",
                columns: new[] { "CompanyId", "LocationId" },
                unique: true,
                filter: "\"LocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingSettings_LocationId",
                table: "CompanyOnboardingSettings",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyOnboardingSettings_Locations_LocationId",
                table: "CompanyOnboardingSettings",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CompanyOnboardingStandingAttachments_Locations_LocationId",
                table: "CompanyOnboardingStandingAttachments",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CompanyOnboardingSettings_Locations_LocationId",
                table: "CompanyOnboardingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_CompanyOnboardingStandingAttachments_Locations_LocationId",
                table: "CompanyOnboardingStandingAttachments");

            migrationBuilder.DropIndex(
                name: "IX_CompanyOnboardingStandingAttachments_LocationId",
                table: "CompanyOnboardingStandingAttachments");

            migrationBuilder.DropIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId",
                table: "CompanyOnboardingSettings");

            migrationBuilder.DropIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId_LocationId",
                table: "CompanyOnboardingSettings");

            migrationBuilder.DropIndex(
                name: "IX_CompanyOnboardingSettings_LocationId",
                table: "CompanyOnboardingSettings");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "CompanyOnboardingStandingAttachments");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "CompanyOnboardingSettings");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyOnboardingSettings_CompanyId",
                table: "CompanyOnboardingSettings",
                column: "CompanyId",
                unique: true);
        }
    }
}

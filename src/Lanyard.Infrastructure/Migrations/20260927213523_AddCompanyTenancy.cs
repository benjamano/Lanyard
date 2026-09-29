using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "StaffDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "ProjectionPrograms",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Playlists",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "GameResults",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Folders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "FileMetadata",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "DmxScenes",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "DmxFixtures",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Dashboards",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Courses",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Clients",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "AutomationRules",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "AppSettings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "Announcements",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            // Everything below existed before tenancy, when Play2Day (company 1) was the only real
            // customer - the column default above puts every existing row there. Rows that can
            // already be tied to a specific company through a location, document type or company
            // logo are moved to that company instead.
            migrationBuilder.Sql(@"
UPDATE ""Courses"" AS c SET ""CompanyId"" = l.""CompanyId""
FROM ""Locations"" AS l WHERE c.""LocationId"" = l.""Id"";

UPDATE ""Announcements"" AS a SET ""CompanyId"" = l.""CompanyId""
FROM ""Locations"" AS l WHERE a.""LocationId"" = l.""Id"";

UPDATE ""StaffDocuments"" AS d SET ""CompanyId"" = t.""CompanyId""
FROM ""StaffDocumentTypes"" AS t WHERE d.""StaffDocumentTypeId"" = t.""Id"";

UPDATE ""FileMetadata"" AS f SET ""CompanyId"" = d.""CompanyId""
FROM ""StaffDocuments"" AS d WHERE d.""FileMetadataId"" = f.""Id"";

UPDATE ""FileMetadata"" AS f SET ""CompanyId"" = a.""CompanyId""
FROM ""CompanyOnboardingStandingAttachments"" AS a WHERE a.""FileMetadataId"" = f.""Id"";

UPDATE ""FileMetadata"" AS f SET ""CompanyId"" = c.""Id""
FROM ""Companies"" AS c WHERE c.""LogoFileId"" = f.""Id"" OR c.""BackgroundImageFileId"" = f.""Id"";
");

            // The default was only there to backfill; new rows get CompanyId from the tenant.
            migrationBuilder.Sql(@"ALTER TABLE ""StaffDocuments"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Songs"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""ProjectionPrograms"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Playlists"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""GameResults"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Folders"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""FileMetadata"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""DmxScenes"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""DmxFixtures"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Dashboards"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Courses"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Clients"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""AutomationRules"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""AppSettings"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");
            migrationBuilder.Sql(@"ALTER TABLE ""Announcements"" ALTER COLUMN ""CompanyId"" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_StaffDocuments_CompanyId",
                table: "StaffDocuments",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_CompanyId",
                table: "Songs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionPrograms_CompanyId",
                table: "ProjectionPrograms",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_CompanyId",
                table: "Playlists",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GameResults_CompanyId",
                table: "GameResults",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_CompanyId",
                table: "Folders",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_CompanyId",
                table: "FileMetadata",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxScenes_CompanyId",
                table: "DmxScenes",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtures_CompanyId",
                table: "DmxFixtures",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_CompanyId",
                table: "Dashboards",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_CompanyId",
                table: "Courses",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_CompanyId",
                table: "Clients",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_CompanyId",
                table: "AutomationRules",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_CompanyId",
                table: "AppSettings",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_CompanyId",
                table: "Announcements",
                column: "CompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Announcements_Companies_CompanyId",
                table: "Announcements",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AppSettings_Companies_CompanyId",
                table: "AppSettings",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AutomationRules_Companies_CompanyId",
                table: "AutomationRules",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Companies_CompanyId",
                table: "Clients",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Courses_Companies_CompanyId",
                table: "Courses",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Dashboards_Companies_CompanyId",
                table: "Dashboards",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DmxFixtures_Companies_CompanyId",
                table: "DmxFixtures",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DmxScenes_Companies_CompanyId",
                table: "DmxScenes",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_Companies_CompanyId",
                table: "FileMetadata",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Companies_CompanyId",
                table: "Folders",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GameResults_Companies_CompanyId",
                table: "GameResults",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_Companies_CompanyId",
                table: "Playlists",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectionPrograms_Companies_CompanyId",
                table: "ProjectionPrograms",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Songs_Companies_CompanyId",
                table: "Songs",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffDocuments_Companies_CompanyId",
                table: "StaffDocuments",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Announcements_Companies_CompanyId",
                table: "Announcements");

            migrationBuilder.DropForeignKey(
                name: "FK_AppSettings_Companies_CompanyId",
                table: "AppSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AutomationRules_Companies_CompanyId",
                table: "AutomationRules");

            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Companies_CompanyId",
                table: "Clients");

            migrationBuilder.DropForeignKey(
                name: "FK_Courses_Companies_CompanyId",
                table: "Courses");

            migrationBuilder.DropForeignKey(
                name: "FK_Dashboards_Companies_CompanyId",
                table: "Dashboards");

            migrationBuilder.DropForeignKey(
                name: "FK_DmxFixtures_Companies_CompanyId",
                table: "DmxFixtures");

            migrationBuilder.DropForeignKey(
                name: "FK_DmxScenes_Companies_CompanyId",
                table: "DmxScenes");

            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_Companies_CompanyId",
                table: "FileMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Companies_CompanyId",
                table: "Folders");

            migrationBuilder.DropForeignKey(
                name: "FK_GameResults_Companies_CompanyId",
                table: "GameResults");

            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_Companies_CompanyId",
                table: "Playlists");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectionPrograms_Companies_CompanyId",
                table: "ProjectionPrograms");

            migrationBuilder.DropForeignKey(
                name: "FK_Songs_Companies_CompanyId",
                table: "Songs");

            migrationBuilder.DropForeignKey(
                name: "FK_StaffDocuments_Companies_CompanyId",
                table: "StaffDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StaffDocuments_CompanyId",
                table: "StaffDocuments");

            migrationBuilder.DropIndex(
                name: "IX_Songs_CompanyId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_ProjectionPrograms_CompanyId",
                table: "ProjectionPrograms");

            migrationBuilder.DropIndex(
                name: "IX_Playlists_CompanyId",
                table: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_GameResults_CompanyId",
                table: "GameResults");

            migrationBuilder.DropIndex(
                name: "IX_Folders_CompanyId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_CompanyId",
                table: "FileMetadata");

            migrationBuilder.DropIndex(
                name: "IX_DmxScenes_CompanyId",
                table: "DmxScenes");

            migrationBuilder.DropIndex(
                name: "IX_DmxFixtures_CompanyId",
                table: "DmxFixtures");

            migrationBuilder.DropIndex(
                name: "IX_Dashboards_CompanyId",
                table: "Dashboards");

            migrationBuilder.DropIndex(
                name: "IX_Courses_CompanyId",
                table: "Courses");

            migrationBuilder.DropIndex(
                name: "IX_Clients_CompanyId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_AutomationRules_CompanyId",
                table: "AutomationRules");

            migrationBuilder.DropIndex(
                name: "IX_AppSettings_CompanyId",
                table: "AppSettings");

            migrationBuilder.DropIndex(
                name: "IX_Announcements_CompanyId",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "StaffDocuments");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ProjectionPrograms");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "GameResults");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "FileMetadata");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "DmxScenes");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "DmxFixtures");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Dashboards");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Announcements");
        }
    }
}

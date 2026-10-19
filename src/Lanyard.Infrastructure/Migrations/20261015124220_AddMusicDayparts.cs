using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicDayparts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clients.LocationId is also added by the kitchen-ordering branch (#113), and the
            // staging database already has it from testing that branch. IF NOT EXISTS keeps
            // this migration from crashing startup there; the result is the same either way.
            migrationBuilder.Sql(@"ALTER TABLE ""Clients"" ADD COLUMN IF NOT EXISTS ""LocationId"" integer;");

            migrationBuilder.CreateTable(
                name: "MusicSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicSchedules_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusicDayparts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MusicScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DaysOfWeek = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "uuid", nullable: true),
                    Volume = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicDayparts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicDayparts_MusicSchedules_MusicScheduleId",
                        column: x => x.MusicScheduleId,
                        principalTable: "MusicSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MusicDayparts_Playlists_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "Playlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Clients_LocationId"" ON ""Clients"" (""LocationId"");");

            migrationBuilder.CreateIndex(
                name: "IX_MusicDayparts_MusicScheduleId",
                table: "MusicDayparts",
                column: "MusicScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicDayparts_PlaylistId",
                table: "MusicDayparts",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicSchedules_LocationId",
                table: "MusicSchedules",
                column: "LocationId",
                unique: true);

            migrationBuilder.Sql(@"ALTER TABLE ""Clients"" DROP CONSTRAINT IF EXISTS ""FK_Clients_Locations_LocationId"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Clients"" ADD CONSTRAINT ""FK_Clients_Locations_LocationId"" FOREIGN KEY (""LocationId"") REFERENCES ""Locations"" (""Id"") ON DELETE SET NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Locations_LocationId",
                table: "Clients");

            migrationBuilder.DropTable(
                name: "MusicDayparts");

            migrationBuilder.DropTable(
                name: "MusicSchedules");

            migrationBuilder.DropIndex(
                name: "IX_Clients_LocationId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Clients");
        }
    }
}

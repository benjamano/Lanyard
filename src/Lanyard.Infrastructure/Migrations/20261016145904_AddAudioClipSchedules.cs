using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioClipSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AudioClipScheduleId",
                table: "AudioClipPlays",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AudioClipSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AudioClipId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    Anchor = table.Column<int>(type: "integer", nullable: false),
                    TimeOfDay = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    OffsetMinutes = table.Column<int>(type: "integer", nullable: false),
                    DaysOfWeek = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastFiredOccurrenceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeleteDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioClipSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioClipSchedules_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AudioClipSchedules_AudioClips_AudioClipId",
                        column: x => x.AudioClipId,
                        principalTable: "AudioClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AudioClipSchedules_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AudioClipSchedules_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipPlays_AudioClipScheduleId",
                table: "AudioClipPlays",
                column: "AudioClipScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipSchedules_AudioClipId",
                table: "AudioClipSchedules",
                column: "AudioClipId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipSchedules_CompanyId",
                table: "AudioClipSchedules",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipSchedules_CreatedByUserId",
                table: "AudioClipSchedules",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipSchedules_LocationId",
                table: "AudioClipSchedules",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AudioClipPlays_AudioClipSchedules_AudioClipScheduleId",
                table: "AudioClipPlays",
                column: "AudioClipScheduleId",
                principalTable: "AudioClipSchedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AudioClipPlays_AudioClipSchedules_AudioClipScheduleId",
                table: "AudioClipPlays");

            migrationBuilder.DropTable(
                name: "AudioClipSchedules");

            migrationBuilder.DropIndex(
                name: "IX_AudioClipPlays_AudioClipScheduleId",
                table: "AudioClipPlays");

            migrationBuilder.DropColumn(
                name: "AudioClipScheduleId",
                table: "AudioClipPlays");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AudioClips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileMetadataId = table.Column<Guid>(type: "uuid", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    Volume = table.Column<int>(type: "integer", nullable: false),
                    DuckToPercent = table.Column<int>(type: "integer", nullable: true),
                    FadeDownMs = table.Column<int>(type: "integer", nullable: true),
                    FadeUpMs = table.Column<int>(type: "integer", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeleteDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioClips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioClips_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AudioClips_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AudioClips_FileMetadata_FileMetadataId",
                        column: x => x.FileMetadataId,
                        principalTable: "FileMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AudioClips_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AudioClipPlays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    AudioClipId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    TriggeredByUserId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioClipPlays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioClipPlays_AspNetUsers_TriggeredByUserId",
                        column: x => x.TriggeredByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AudioClipPlays_AudioClips_AudioClipId",
                        column: x => x.AudioClipId,
                        principalTable: "AudioClips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AudioClipPlays_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AudioClipPlays_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipPlays_AudioClipId",
                table: "AudioClipPlays",
                column: "AudioClipId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipPlays_ClientId_RequestedAtUtc",
                table: "AudioClipPlays",
                columns: new[] { "ClientId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipPlays_CompanyId",
                table: "AudioClipPlays",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClipPlays_TriggeredByUserId",
                table: "AudioClipPlays",
                column: "TriggeredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClips_CompanyId",
                table: "AudioClips",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClips_CreatedByUserId",
                table: "AudioClips",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClips_FileMetadataId",
                table: "AudioClips",
                column: "FileMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioClips_LocationId",
                table: "AudioClips",
                column: "LocationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioClipPlays");

            migrationBuilder.DropTable(
                name: "AudioClips");
        }
    }
}

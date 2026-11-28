using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeechAudioClips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every clip so far is an uploaded file (AudioClipKind.File = 1).
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "AudioClips",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "SpeechPitch",
                table: "AudioClips",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpeechRate",
                table: "AudioClips",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpeechRenderKey",
                table: "AudioClips",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpeechText",
                table: "AudioClips",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpeechVoiceId",
                table: "AudioClips",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AudioClips");

            migrationBuilder.DropColumn(
                name: "SpeechPitch",
                table: "AudioClips");

            migrationBuilder.DropColumn(
                name: "SpeechRate",
                table: "AudioClips");

            migrationBuilder.DropColumn(
                name: "SpeechRenderKey",
                table: "AudioClips");

            migrationBuilder.DropColumn(
                name: "SpeechText",
                table: "AudioClips");

            migrationBuilder.DropColumn(
                name: "SpeechVoiceId",
                table: "AudioClips");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LanyardApp.Migrations
{
    /// <inheritdoc />
    public partial class addingAlbumNameToSongModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlbumName",
                table: "Songs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlbumName",
                table: "Songs");
        }
    }
}

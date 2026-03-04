using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class addingLoopingToProjectionProgramSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RepeatInfinitely",
                table: "ClientProjectionSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RepeatNumberOfTimes",
                table: "ClientProjectionSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepeatInfinitely",
                table: "ClientProjectionSettings");

            migrationBuilder.DropColumn(
                name: "RepeatNumberOfTimes",
                table: "ClientProjectionSettings");
        }
    }
}

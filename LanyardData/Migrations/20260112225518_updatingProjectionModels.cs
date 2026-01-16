using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class updatingProjectionModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VideoSourceIndex",
                table: "ProjectionProgramSteps");

            migrationBuilder.RenameColumn(
                name: "Webpage",
                table: "ProjectionProgramSteps",
                newName: "Source");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ProjectionProgramSteps",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "Width",
                table: "ClientProjectionSettings",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "Height",
                table: "ClientProjectionSettings",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ProjectionProgramSteps");

            migrationBuilder.RenameColumn(
                name: "Source",
                table: "ProjectionProgramSteps",
                newName: "Webpage");

            migrationBuilder.AddColumn<int>(
                name: "VideoSourceIndex",
                table: "ProjectionProgramSteps",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Width",
                table: "ClientProjectionSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Height",
                table: "ClientProjectionSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}

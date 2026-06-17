using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdatingDashboardModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfigJson",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "DashboardWidgets");

            migrationBuilder.Sql(@"
ALTER TABLE ""DashboardWidgets""
ALTER COLUMN ""Type"" TYPE integer
USING CASE
    WHEN ""Type"" ~ '^\d+$' THEN ""Type""::integer
    WHEN ""Type"" = 'DigitalClock' THEN 1
    WHEN ""Type"" = 'TextArea' THEN 2
    WHEN ""Type"" = 'Clock' THEN 1
    WHEN ""Type"" = 'MusicControls' THEN 2
    ELSE 1
END;
");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Dashboards",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Dashboards",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "DashboardWidgets",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "ConfigJson",
                table: "DashboardWidgets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "DashboardWidgets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Dashboards",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Dashboards",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}

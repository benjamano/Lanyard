using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingButtonWidgetProjectionTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActionType",
                table: "DashboardWidgets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ButtonWidget_ClientId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectionProgramId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionType",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ButtonWidget_ClientId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ProjectionProgramId",
                table: "DashboardWidgets");
        }
    }
}

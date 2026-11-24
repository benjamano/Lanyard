using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskChecklistCard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ChecklistCardAllowKioskTicking",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ChecklistCardChecklistId",
                table: "DashboardWidgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChecklistCardHideWhenDone",
                table: "DashboardWidgets",
                type: "boolean",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "ChecklistCompletions",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "ChecklistCompletions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChecklistCompletions_ClientId",
                table: "ChecklistCompletions",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChecklistCompletions_Clients_ClientId",
                table: "ChecklistCompletions",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChecklistCompletions_Clients_ClientId",
                table: "ChecklistCompletions");

            migrationBuilder.DropIndex(
                name: "IX_ChecklistCompletions_ClientId",
                table: "ChecklistCompletions");

            migrationBuilder.DropColumn(
                name: "ChecklistCardAllowKioskTicking",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ChecklistCardChecklistId",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ChecklistCardHideWhenDone",
                table: "DashboardWidgets");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "ChecklistCompletions");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "ChecklistCompletions",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}

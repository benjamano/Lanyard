using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictChecklistTickDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChecklistCompletions_ChecklistRunItems_RunItemId",
                table: "ChecklistCompletions");

            migrationBuilder.AddForeignKey(
                name: "FK_ChecklistCompletions_ChecklistRunItems_RunItemId",
                table: "ChecklistCompletions",
                column: "RunItemId",
                principalTable: "ChecklistRunItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChecklistCompletions_ChecklistRunItems_RunItemId",
                table: "ChecklistCompletions");

            migrationBuilder.AddForeignKey(
                name: "FK_ChecklistCompletions_ChecklistRunItems_RunItemId",
                table: "ChecklistCompletions",
                column: "RunItemId",
                principalTable: "ChecklistRunItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatSuspensionCompanyForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ChatSuspensions_CompanyId",
                table: "ChatSuspensions",
                column: "CompanyId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatSuspensions_Companies_CompanyId",
                table: "ChatSuspensions",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatSuspensions_Companies_CompanyId",
                table: "ChatSuspensions");

            migrationBuilder.DropIndex(
                name: "IX_ChatSuspensions_CompanyId",
                table: "ChatSuspensions");
        }
    }
}

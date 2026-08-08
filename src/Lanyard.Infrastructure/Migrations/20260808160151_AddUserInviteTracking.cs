using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserInviteTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InvitedDate",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordSetDate",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts that already have a usable password predate the invite flow entirely -
            // without this, every existing user would show as "Invite Pending" in the admin grid.
            migrationBuilder.Sql(
                """UPDATE "AspNetUsers" SET "PasswordSetDate" = NOW() WHERE "PasswordHash" IS NOT NULL""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InvitedDate",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PasswordSetDate",
                table: "AspNetUsers");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveDevIdentitySeedToModelCreating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: "dev-admin-user",
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAEJ1AhlJOAablYfFpSBJmkOkqLkqidbamfdrRwkTGjXCnkD30AqM6PNAcAh96mQgYXg==");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: "dev-admin-user",
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAED21DC/16ShRY4QPBD7gJrWtwD6i3kVQ78rGF6yXmgl7Ayr92rUw0AyXaQnUPgrvqw==");
        }
    }
}

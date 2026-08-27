using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserErasureAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserErasureRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ErasedUserId = table.Column<string>(type: "text", nullable: false),
                    ErasedEmailHash = table.Column<string>(type: "text", nullable: false),
                    ErasedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PerformedByUserId = table.Column<string>(type: "text", nullable: false),
                    PerformedByUserName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserErasureRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserErasureRecords_ErasedAtUtc",
                table: "UserErasureRecords",
                column: "ErasedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserErasureRecords");
        }
    }
}

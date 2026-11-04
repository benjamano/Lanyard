using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskEnrolmentRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientEnrolmentRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairingCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PollTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MachineName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReportedVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RequestIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedCompanyId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientEnrolmentRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientEnrolmentRequests_ClientId",
                table: "ClientEnrolmentRequests",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientEnrolmentRequests_PairingCodeHash_Status",
                table: "ClientEnrolmentRequests",
                columns: new[] { "PairingCodeHash", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientEnrolmentRequests");
        }
    }
}

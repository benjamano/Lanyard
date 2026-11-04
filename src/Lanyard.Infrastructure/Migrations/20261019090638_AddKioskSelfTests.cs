using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKioskSelfTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientSelfTestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "text", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClientVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FlashRequested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSelfTestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientSelfTestRuns_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClientSelfTestRuns_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientSelfTestRuns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientSelfTestChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSelfTestChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientSelfTestChecks_ClientSelfTestRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ClientSelfTestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSelfTestChecks_RunId",
                table: "ClientSelfTestChecks",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSelfTestRuns_ClientId_RequestedAtUtc",
                table: "ClientSelfTestRuns",
                columns: new[] { "ClientId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSelfTestRuns_CompanyId",
                table: "ClientSelfTestRuns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSelfTestRuns_RequestedAtUtc",
                table: "ClientSelfTestRuns",
                column: "RequestedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSelfTestRuns_RequestedByUserId",
                table: "ClientSelfTestRuns",
                column: "RequestedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientSelfTestChecks");

            migrationBuilder.DropTable(
                name: "ClientSelfTestRuns");
        }
    }
}

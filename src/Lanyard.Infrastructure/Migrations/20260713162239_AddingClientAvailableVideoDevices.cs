using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingClientAvailableVideoDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientAvailableVideoDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAvailableVideoDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientAvailableVideoDevices_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAvailableVideoDevices_ClientId",
                table: "ClientAvailableVideoDevices",
                column: "ClientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientAvailableVideoDevices");
        }
    }
}

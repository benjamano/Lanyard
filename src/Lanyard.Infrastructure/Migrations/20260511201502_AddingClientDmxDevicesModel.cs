using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingClientDmxDevicesModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientDMXConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDMXConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientDMXConfigurations_AspNetUsers_UpdatedByUserId",
                table: "ClientDMXConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientDMXConfigurations_Clients_ClientId",
                table: "ClientDMXConfigurations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientDMXConfigurations",
                table: "ClientDMXConfigurations");

            migrationBuilder.RenameTable(
                name: "ClientDMXConfigurations",
                newName: "ClientDmxConfigurations");

            migrationBuilder.RenameIndex(
                name: "IX_ClientDMXConfigurations_UpdatedByUserId",
                table: "ClientDmxConfigurations",
                newName: "IX_ClientDmxConfigurations_UpdatedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_ClientDMXConfigurations_CreateByUserId",
                table: "ClientDmxConfigurations",
                newName: "IX_ClientDmxConfigurations_CreateByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_ClientDMXConfigurations_ClientId",
                table: "ClientDmxConfigurations",
                newName: "IX_ClientDmxConfigurations_ClientId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientDmxConfigurations",
                table: "ClientDmxConfigurations",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "ClientAvailableDmxDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceIndex = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAvailableDmxDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientAvailableDmxDevices_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAvailableDmxDevices_ClientId",
                table: "ClientAvailableDmxDevices",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDmxConfigurations",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_UpdatedByUserId",
                table: "ClientDmxConfigurations",
                column: "UpdatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDmxConfigurations_Clients_ClientId",
                table: "ClientDmxConfigurations",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_UpdatedByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_Clients_ClientId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropTable(
                name: "ClientAvailableDmxDevices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientDmxConfigurations",
                table: "ClientDmxConfigurations");

            migrationBuilder.RenameTable(
                name: "ClientDmxConfigurations",
                newName: "ClientDMXConfigurations");

            migrationBuilder.RenameIndex(
                name: "IX_ClientDmxConfigurations_UpdatedByUserId",
                table: "ClientDMXConfigurations",
                newName: "IX_ClientDMXConfigurations_UpdatedByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_ClientDmxConfigurations_CreateByUserId",
                table: "ClientDMXConfigurations",
                newName: "IX_ClientDMXConfigurations_CreateByUserId");

            migrationBuilder.RenameIndex(
                name: "IX_ClientDmxConfigurations_ClientId",
                table: "ClientDMXConfigurations",
                newName: "IX_ClientDMXConfigurations_ClientId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientDMXConfigurations",
                table: "ClientDMXConfigurations",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDMXConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDMXConfigurations",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDMXConfigurations_AspNetUsers_UpdatedByUserId",
                table: "ClientDMXConfigurations",
                column: "UpdatedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDMXConfigurations_Clients_ClientId",
                table: "ClientDMXConfigurations",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

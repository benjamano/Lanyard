using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingDmxScenes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_UpdatedByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_ClientDmxConfigurations_UpdatedByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.AlterColumn<string>(
                name: "CreateByUserId",
                table: "ClientDmxConfigurations",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateByUserId",
                table: "ClientDmxConfigurations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DmxScenes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxScenes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxScenes_AspNetUsers_CreateByUserId",
                        column: x => x.CreateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DmxScenes_AspNetUsers_UpdateByUserId",
                        column: x => x.UpdateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DmxScenes_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientDmxConfigurations_UpdateByUserId",
                table: "ClientDmxConfigurations",
                column: "UpdateByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxScenes_ClientId",
                table: "DmxScenes",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxScenes_CreateByUserId",
                table: "DmxScenes",
                column: "CreateByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxScenes_UpdateByUserId",
                table: "DmxScenes",
                column: "UpdateByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDmxConfigurations",
                column: "CreateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_UpdateByUserId",
                table: "ClientDmxConfigurations",
                column: "UpdateByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_CreateByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientDmxConfigurations_AspNetUsers_UpdateByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropTable(
                name: "DmxScenes");

            migrationBuilder.DropIndex(
                name: "IX_ClientDmxConfigurations_UpdateByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.DropColumn(
                name: "UpdateByUserId",
                table: "ClientDmxConfigurations");

            migrationBuilder.AlterColumn<string>(
                name: "CreateByUserId",
                table: "ClientDmxConfigurations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "ClientDmxConfigurations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UpdatedByUserId",
                table: "ClientDmxConfigurations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ClientDmxConfigurations_UpdatedByUserId",
                table: "ClientDmxConfigurations",
                column: "UpdatedByUserId");

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
        }
    }
}

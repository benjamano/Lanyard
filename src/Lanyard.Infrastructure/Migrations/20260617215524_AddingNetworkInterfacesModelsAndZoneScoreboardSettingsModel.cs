using System;
using System.Net.NetworkInformation;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingNetworkInterfacesModelsAndZoneScoreboardSettingsModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientAvailableNetworkInterfaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    MacAddress = table.Column<PhysicalAddress>(type: "macaddr", nullable: false),
                    LastSeenDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientAvailableNetworkInterfaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientAvailableNetworkInterfaces_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ZoneScoreboardSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreferredDeviceMacAddress = table.Column<string>(type: "text", nullable: false),
                    ZoneScoreboardVersion = table.Column<int>(type: "integer", nullable: false),
                    DestinationIp = table.Column<string>(type: "text", nullable: true),
                    SourceIp = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneScoreboardSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZoneScoreboardSettings_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientAvailableNetworkInterfaces_ClientId",
                table: "ClientAvailableNetworkInterfaces",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ZoneScoreboardSettings_ClientId",
                table: "ZoneScoreboardSettings",
                column: "ClientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientAvailableNetworkInterfaces");

            migrationBuilder.DropTable(
                name: "ZoneScoreboardSettings");
        }
    }
}

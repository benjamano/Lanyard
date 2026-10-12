using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientHealthSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientHealthSnapshots",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstHeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProcessStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MachineUptimeSeconds = table.Column<long>(type: "bigint", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: true),
                    MemoryPercent = table.Column<double>(type: "double precision", nullable: true),
                    SystemDriveFreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    SystemDriveTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DmxConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    DmxOpen = table.Column<bool>(type: "boolean", nullable: false),
                    PreferredAudioDevicePresent = table.Column<bool>(type: "boolean", nullable: true),
                    PacketCaptureConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    PacketCaptureRunning = table.Column<bool>(type: "boolean", nullable: false),
                    ProjectionRunningCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientHealthSnapshots", x => x.ClientId);
                    table.ForeignKey(
                        name: "FK_ClientHealthSnapshots_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientHealthSnapshots");
        }
    }
}

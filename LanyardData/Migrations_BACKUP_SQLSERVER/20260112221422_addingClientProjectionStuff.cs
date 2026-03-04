using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class addingClientProjectionStuff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectionPrograms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionPrograms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientProjectionSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayIndex = table.Column<int>(type: "int", nullable: false),
                    ProjectionProgramId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsFullScreen = table.Column<bool>(type: "bit", nullable: false),
                    IsBorderless = table.Column<bool>(type: "bit", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientProjectionSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientProjectionSettings_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientProjectionSettings_ProjectionPrograms_ProjectionProgramId",
                        column: x => x.ProjectionProgramId,
                        principalTable: "ProjectionPrograms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectionProgramSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectionProgramId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    VideoSourceIndex = table.Column<int>(type: "int", nullable: true),
                    Webpage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionProgramSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectionProgramSteps_ProjectionPrograms_ProjectionProgramId",
                        column: x => x.ProjectionProgramId,
                        principalTable: "ProjectionPrograms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientProjectionSettings_ClientId",
                table: "ClientProjectionSettings",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientProjectionSettings_ProjectionProgramId",
                table: "ClientProjectionSettings",
                column: "ProjectionProgramId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionProgramSteps_ProjectionProgramId",
                table: "ProjectionProgramSteps",
                column: "ProjectionProgramId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientProjectionSettings");

            migrationBuilder.DropTable(
                name: "ProjectionProgramSteps");

            migrationBuilder.DropTable(
                name: "ProjectionPrograms");
        }
    }
}

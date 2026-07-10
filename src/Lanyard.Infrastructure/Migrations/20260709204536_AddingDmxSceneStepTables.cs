using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingDmxSceneStepTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DmxSceneSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxSceneSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxSceneSteps_AspNetUsers_CreateByUserId",
                        column: x => x.CreateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DmxSceneSteps_AspNetUsers_UpdateByUserId",
                        column: x => x.UpdateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DmxSceneSteps_DmxScenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "DmxScenes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DmxSceneStepChannelValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SceneStepId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelNumber = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<byte>(type: "smallint", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxSceneStepChannelValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxSceneStepChannelValues_AspNetUsers_CreateByUserId",
                        column: x => x.CreateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DmxSceneStepChannelValues_AspNetUsers_UpdateByUserId",
                        column: x => x.UpdateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_DmxSceneStepChannelValues_DmxSceneSteps_SceneStepId",
                        column: x => x.SceneStepId,
                        principalTable: "DmxSceneSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneStepChannelValues_CreateByUserId",
                table: "DmxSceneStepChannelValues",
                column: "CreateByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneStepChannelValues_SceneStepId",
                table: "DmxSceneStepChannelValues",
                column: "SceneStepId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneStepChannelValues_UpdateByUserId",
                table: "DmxSceneStepChannelValues",
                column: "UpdateByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneSteps_CreateByUserId",
                table: "DmxSceneSteps",
                column: "CreateByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneSteps_SceneId",
                table: "DmxSceneSteps",
                column: "SceneId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneSteps_UpdateByUserId",
                table: "DmxSceneSteps",
                column: "UpdateByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DmxSceneStepChannelValues");

            migrationBuilder.DropTable(
                name: "DmxSceneSteps");
        }
    }
}

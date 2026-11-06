using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftTemplateRegulars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShiftTemplateSlotRegulars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftTemplateSlotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftTemplateSlotRegulars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShiftTemplateSlotRegulars_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShiftTemplateSlotRegulars_ShiftTemplateSlots_ShiftTemplateS~",
                        column: x => x.ShiftTemplateSlotId,
                        principalTable: "ShiftTemplateSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftTemplateSlotRegulars_ShiftTemplateSlotId_UserId",
                table: "ShiftTemplateSlotRegulars",
                columns: new[] { "ShiftTemplateSlotId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShiftTemplateSlotRegulars_UserId",
                table: "ShiftTemplateSlotRegulars",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShiftTemplateSlotRegulars");
        }
    }
}

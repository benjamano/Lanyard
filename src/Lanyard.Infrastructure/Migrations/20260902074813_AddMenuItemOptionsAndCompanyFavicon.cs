using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMenuItemOptionsAndCompanyFavicon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FaviconFileId",
                table: "Companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MenuItemOptionGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MenuItemId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MinSelections = table.Column<int>(type: "integer", nullable: false),
                    MaxSelections = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuItemOptionGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuItemOptionGroups_MenuItems_MenuItemId",
                        column: x => x.MenuItemId,
                        principalTable: "MenuItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MenuItemOptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OptionGroupId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PriceDeltaCents = table.Column<int>(type: "integer", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContainsAllergens = table.Column<int>(type: "integer", nullable: false),
                    MayContainAllergens = table.Column<int>(type: "integer", nullable: false),
                    AllergensConfirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuItemOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MenuItemOptions_MenuItemOptionGroups_OptionGroupId",
                        column: x => x.OptionGroupId,
                        principalTable: "MenuItemOptionGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KitchenOrderItemOptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderItemId = table.Column<int>(type: "integer", nullable: false),
                    MenuItemOptionId = table.Column<int>(type: "integer", nullable: true),
                    GroupNameSnapshot = table.Column<string>(type: "text", nullable: false),
                    OptionNameSnapshot = table.Column<string>(type: "text", nullable: false),
                    PriceDeltaCentsSnapshot = table.Column<int>(type: "integer", nullable: false),
                    ContainsAllergensSnapshot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KitchenOrderItemOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KitchenOrderItemOptions_KitchenOrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "KitchenOrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KitchenOrderItemOptions_MenuItemOptions_MenuItemOptionId",
                        column: x => x.MenuItemOptionId,
                        principalTable: "MenuItemOptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_FaviconFileId",
                table: "Companies",
                column: "FaviconFileId");

            migrationBuilder.CreateIndex(
                name: "IX_KitchenOrderItemOptions_MenuItemOptionId",
                table: "KitchenOrderItemOptions",
                column: "MenuItemOptionId");

            migrationBuilder.CreateIndex(
                name: "IX_KitchenOrderItemOptions_OrderItemId",
                table: "KitchenOrderItemOptions",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItemOptionGroups_MenuItemId",
                table: "MenuItemOptionGroups",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MenuItemOptions_OptionGroupId",
                table: "MenuItemOptions",
                column: "OptionGroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_FileMetadata_FaviconFileId",
                table: "Companies",
                column: "FaviconFileId",
                principalTable: "FileMetadata",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_FileMetadata_FaviconFileId",
                table: "Companies");

            migrationBuilder.DropTable(
                name: "KitchenOrderItemOptions");

            migrationBuilder.DropTable(
                name: "MenuItemOptions");

            migrationBuilder.DropTable(
                name: "MenuItemOptionGroups");

            migrationBuilder.DropIndex(
                name: "IX_Companies_FaviconFileId",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "FaviconFileId",
                table: "Companies");
        }
    }
}

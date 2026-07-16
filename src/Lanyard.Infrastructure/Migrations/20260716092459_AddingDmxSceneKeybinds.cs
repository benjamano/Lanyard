using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingDmxSceneKeybinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Empty-array default backfills existing rows; a NOT NULL text[] with no
            // default would fail on a populated table.
            migrationBuilder.AddColumn<List<string>>(
                name: "KeyBindings",
                table: "DmxScenes",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyBindings",
                table: "DmxScenes");
        }
    }
}

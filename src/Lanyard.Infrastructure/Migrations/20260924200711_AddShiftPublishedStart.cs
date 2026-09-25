using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftPublishedStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedStartUtc",
                table: "Shifts",
                type: "timestamp with time zone",
                nullable: true);

            // Shifts published before this column existed started wherever they are now as far as
            // anyone was told, so that's the best available snapshot.
            migrationBuilder.Sql("UPDATE \"Shifts\" SET \"PublishedStartUtc\" = \"StartUtc\" WHERE \"PublishedDateUtc\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedStartUtc",
                table: "Shifts");
        }
    }
}

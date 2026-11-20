using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyDocumentTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartyDocumentTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Document = table.Column<int>(type: "integer", nullable: false),
                    ArtworkFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    WidthMm = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: false),
                    HeightMm = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: false),
                    Font = table.Column<int>(type: "integer", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false),
                    VoucherValidityMonths = table.Column<int>(type: "integer", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartyDocumentTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartyDocumentTemplates_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartyDocumentTemplates_CompanyId_Document",
                table: "PartyDocumentTemplates",
                columns: new[] { "CompanyId", "Document" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartyDocumentTemplates");
        }
    }
}

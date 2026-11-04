using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDmxFixtureDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "FixtureType",
                table: "DmxFixtures",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            // Nullable until every existing fixture has been pointed at its built-in mode below.
            migrationBuilder.AddColumn<Guid>(
                name: "FixtureModeId",
                table: "DmxFixtures",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DmxFixtureDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OflKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OflRevision = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxFixtureDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxFixtureDefinitions_AspNetUsers_CreateByUserId",
                        column: x => x.CreateByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DmxFixtureDefinitions_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DmxFixtureModes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ShortName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ChannelCount = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxFixtureModes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxFixtureModes_DmxFixtureDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "DmxFixtureDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DmxFixtureModeChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Offset = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CoarseOffset = table.Column<int>(type: "integer", nullable: true),
                    FineLevel = table.Column<byte>(type: "smallint", nullable: false),
                    DefaultValue = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxFixtureModeChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxFixtureModeChannels_DmxFixtureModes_ModeId",
                        column: x => x.ModeId,
                        principalTable: "DmxFixtureModes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DmxFixtureModeChannelColorRanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    DmxStart = table.Column<byte>(type: "smallint", nullable: false),
                    DmxEnd = table.Column<byte>(type: "smallint", nullable: false),
                    ColorHex = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmxFixtureModeChannelColorRanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DmxFixtureModeChannelColorRanges_DmxFixtureModeChannels_Mod~",
                        column: x => x.ModeChannelId,
                        principalTable: "DmxFixtureModeChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DmxFixtureDefinitions",
                columns: new[] { "Id", "CompanyId", "CreateByUserId", "CreateDate", "IsActive", "Manufacturer", "Model", "OflKey", "OflRevision", "Source" },
                values: new object[,]
                {
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000001"), null, null, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), true, "Generic", "Dimmer", null, null, 0 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000002"), null, null, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), true, "Generic", "RGB", null, null, 0 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000003"), null, null, new DateTime(2026, 10, 12, 0, 0, 0, 0, DateTimeKind.Utc), true, "Generic", "RGB + Dimmer", null, null, 0 }
                });

            migrationBuilder.InsertData(
                table: "DmxFixtureModes",
                columns: new[] { "Id", "ChannelCount", "DefinitionId", "Name", "ShortName", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000101"), 1, new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000001"), "1-channel", "1ch", 0 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000102"), 3, new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000002"), "3-channel", "3ch", 0 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000103"), 4, new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000003"), "4-channel", "4ch", 0 }
                });

            migrationBuilder.InsertData(
                table: "DmxFixtureModeChannels",
                columns: new[] { "Id", "CoarseOffset", "DefaultValue", "FineLevel", "Label", "ModeId", "Offset", "Role" },
                values: new object[,]
                {
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001001"), null, (byte)0, (byte)0, "Dimmer", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000101"), 0, 2 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001002"), null, (byte)0, (byte)0, "Red", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000102"), 0, 10 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001003"), null, (byte)0, (byte)0, "Green", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000102"), 1, 11 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001004"), null, (byte)0, (byte)0, "Blue", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000102"), 2, 12 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001005"), null, (byte)0, (byte)0, "Red", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000103"), 0, 10 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001006"), null, (byte)0, (byte)0, "Green", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000103"), 1, 11 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001007"), null, (byte)0, (byte)0, "Blue", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000103"), 2, 12 },
                    { new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000001008"), null, (byte)0, (byte)0, "Dimmer", new Guid("6f1d6a8e-1c1b-4c55-9a0e-000000000103"), 3, 2 }
                });

            // Every fixture that predates definitions becomes the built-in mode for its old
            // FixtureType (Dimmer = 0, Rgb = 1, RgbDimmer = 2), with identical channel offsets.
            migrationBuilder.Sql(@"
                UPDATE ""DmxFixtures""
                SET ""FixtureModeId"" = CASE ""FixtureType""
                    WHEN 0 THEN '6f1d6a8e-1c1b-4c55-9a0e-000000000101'::uuid
                    WHEN 1 THEN '6f1d6a8e-1c1b-4c55-9a0e-000000000102'::uuid
                    WHEN 2 THEN '6f1d6a8e-1c1b-4c55-9a0e-000000000103'::uuid
                END
                WHERE ""FixtureModeId"" IS NULL;");

            migrationBuilder.AlterColumn<Guid>(
                name: "FixtureModeId",
                table: "DmxFixtures",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtures_FixtureModeId",
                table: "DmxFixtures",
                column: "FixtureModeId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtureDefinitions_CompanyId_OflKey_OflRevision",
                table: "DmxFixtureDefinitions",
                columns: new[] { "CompanyId", "OflKey", "OflRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtureDefinitions_CreateByUserId",
                table: "DmxFixtureDefinitions",
                column: "CreateByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtureModeChannelColorRanges_ModeChannelId",
                table: "DmxFixtureModeChannelColorRanges",
                column: "ModeChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtureModeChannels_ModeId_Offset",
                table: "DmxFixtureModeChannels",
                columns: new[] { "ModeId", "Offset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DmxFixtureModes_DefinitionId",
                table: "DmxFixtureModes",
                column: "DefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_DmxFixtures_DmxFixtureModes_FixtureModeId",
                table: "DmxFixtures",
                column: "FixtureModeId",
                principalTable: "DmxFixtureModes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DmxFixtures_DmxFixtureModes_FixtureModeId",
                table: "DmxFixtures");

            migrationBuilder.DropTable(
                name: "DmxFixtureModeChannelColorRanges");

            migrationBuilder.DropTable(
                name: "DmxFixtureModeChannels");

            migrationBuilder.DropTable(
                name: "DmxFixtureModes");

            migrationBuilder.DropTable(
                name: "DmxFixtureDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_DmxFixtures_FixtureModeId",
                table: "DmxFixtures");

            migrationBuilder.DropColumn(
                name: "FixtureModeId",
                table: "DmxFixtures");

            // Lossy: fixtures patched with an imported or custom fixture have no old type, so they
            // fall back to RGB, and their definitions are gone with the tables above.
            migrationBuilder.Sql(@"UPDATE ""DmxFixtures"" SET ""FixtureType"" = 1 WHERE ""FixtureType"" IS NULL;");

            migrationBuilder.AlterColumn<int>(
                name: "FixtureType",
                table: "DmxFixtures",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}

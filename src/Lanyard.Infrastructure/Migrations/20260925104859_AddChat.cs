using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowMessagePreviews",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                // Existing accounts keep seeing message text, the same as new ones.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ChatBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockerUserId = table.Column<string>(type: "text", nullable: false),
                    BlockedUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatBlocks_AspNetUsers_BlockedUserId",
                        column: x => x.BlockedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatBlocks_AspNetUsers_BlockerUserId",
                        column: x => x.BlockerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    DirectKey = table.Column<string>(type: "text", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatConversations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChatSuspensions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    CompanyId = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ImposedByUserId = table.Column<string>(type: "text", nullable: false),
                    LiftedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LiftedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSuspensions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatSuspensions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    JoinedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeftUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastReadUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDigestEmailUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsMuted = table.Column<bool>(type: "boolean", nullable: false),
                    IsGroupAdmin = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMembers_ChatConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "ChatConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<string>(type: "text", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: false),
                    CreateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EditedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedByUserId = table.Column<string>(type: "text", nullable: true),
                    ReplyToMessageId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_AspNetUsers_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "ChatConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatMessages_ReplyToMessageId",
                        column: x => x.ReplyToMessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChatReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterUserId = table.Column<string>(type: "text", nullable: false),
                    ReportedUserId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    MessageHtmlSnapshot = table.Column<string>(type: "text", nullable: false),
                    MessageSentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReviewedByUserId = table.Column<string>(type: "text", nullable: true),
                    ReviewedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatReports_AspNetUsers_ReportedUserId",
                        column: x => x.ReportedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChatReports_AspNetUsers_ReporterUserId",
                        column: x => x.ReporterUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChatReports_ChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChatReports_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatBlocks_BlockedUserId",
                table: "ChatBlocks",
                column: "BlockedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatBlocks_BlockerUserId_BlockedUserId",
                table: "ChatBlocks",
                columns: new[] { "BlockerUserId", "BlockedUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversations_CompanyId_LastMessageUtc",
                table: "ChatConversations",
                columns: new[] { "CompanyId", "LastMessageUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversations_DirectKey",
                table: "ChatConversations",
                column: "DirectKey",
                unique: true,
                filter: "\"DirectKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMembers_ConversationId_UserId",
                table: "ChatMembers",
                columns: new[] { "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMembers_UserId",
                table: "ChatMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_AuthorUserId",
                table: "ChatMessages",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_CreateUtc",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "CreateUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ReplyToMessageId",
                table: "ChatMessages",
                column: "ReplyToMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatReports_LocationId_Status",
                table: "ChatReports",
                columns: new[] { "LocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatReports_MessageId",
                table: "ChatReports",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatReports_ReportedUserId",
                table: "ChatReports",
                column: "ReportedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatReports_ReporterUserId",
                table: "ChatReports",
                column: "ReporterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSuspensions_UserId_CompanyId",
                table: "ChatSuspensions",
                columns: new[] { "UserId", "CompanyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatBlocks");

            migrationBuilder.DropTable(
                name: "ChatMembers");

            migrationBuilder.DropTable(
                name: "ChatReports");

            migrationBuilder.DropTable(
                name: "ChatSuspensions");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "ChatConversations");

            migrationBuilder.DropColumn(
                name: "ShowMessagePreviews",
                table: "AspNetUsers");
        }
    }
}

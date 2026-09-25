using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChatChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "ChatMessages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ChatMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "LinkedEntityId",
                table: "ChatMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinnedByUserId",
                table: "ChatMessages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedUtc",
                table: "ChatMessages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StaffCanPost",
                table: "ChatConversations",
                type: "boolean",
                nullable: false,
                defaultValue: true); // Existing conversations: staff can post, as before.

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ConversationId_IsPinned",
                table: "ChatMessages",
                columns: new[] { "ConversationId", "IsPinned" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversations_CompanyChannel",
                table: "ChatConversations",
                column: "CompanyId",
                unique: true,
                filter: "\"Kind\" = 3");

            migrationBuilder.CreateIndex(
                name: "IX_ChatConversations_LocationId",
                table: "ChatConversations",
                column: "LocationId",
                unique: true,
                filter: "\"Kind\" = 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_ConversationId_IsPinned",
                table: "ChatMessages");

            migrationBuilder.DropIndex(
                name: "IX_ChatConversations_CompanyChannel",
                table: "ChatConversations");

            migrationBuilder.DropIndex(
                name: "IX_ChatConversations_LocationId",
                table: "ChatConversations");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "LinkedEntityId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "PinnedByUserId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "PinnedUtc",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "StaffCanPost",
                table: "ChatConversations");
        }
    }
}

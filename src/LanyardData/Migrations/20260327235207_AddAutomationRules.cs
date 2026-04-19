using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_Folders_FolderId",
                table: "FileMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_Name",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_FileMetadata_FileName",
                table: "FileMetadata");

            migrationBuilder.DropIndex(
                name: "IX_DashboardWidgets_DashboardId_IsActive_SortOrder",
                table: "DashboardWidgets");

            migrationBuilder.DropIndex(
                name: "IX_Dashboards_IsActive_Name",
                table: "Dashboards");

            migrationBuilder.DeleteData(
                table: "AspNetUserRoles",
                keyColumns: new[] { "RoleId", "UserId" },
                keyValues: new object[] { "dev-role-admin", "dev-admin-user" });

            migrationBuilder.DeleteData(
                table: "AspNetUserRoles",
                keyColumns: new[] { "RoleId", "UserId" },
                keyValues: new object[] { "dev-role-can-clock-in", "dev-admin-user" });

            migrationBuilder.DeleteData(
                table: "AspNetUserRoles",
                keyColumns: new[] { "RoleId", "UserId" },
                keyValues: new object[] { "dev-role-can-control-music", "dev-admin-user" });

            migrationBuilder.DeleteData(
                table: "AspNetUserRoles",
                keyColumns: new[] { "RoleId", "UserId" },
                keyValues: new object[] { "dev-role-manager", "dev-admin-user" });

            migrationBuilder.DeleteData(
                table: "AspNetUserRoles",
                keyColumns: new[] { "RoleId", "UserId" },
                keyValues: new object[] { "dev-role-staff", "dev-admin-user" });

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "dev-role-admin");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "dev-role-can-clock-in");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "dev-role-can-control-music");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "dev-role-manager");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "dev-role-staff");

            migrationBuilder.DeleteData(
                table: "AspNetUsers",
                keyColumn: "Id",
                keyValue: "dev-admin-user");

            migrationBuilder.CreateTable(
                name: "AutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    TriggerClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerEvent = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRules_Clients_TriggerClientId",
                        column: x => x.TriggerClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuleActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "text", nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuleActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuleActions_AutomationRules_AutomationRuleId",
                        column: x => x.AutomationRuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuleExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TriggerEvent = table.Column<string>(type: "text", nullable: false),
                    TriggerClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    OverallSuccess = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuleExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuleExecutions_AutomationRules_AutomationRuleId",
                        column: x => x.AutomationRuleId,
                        principalTable: "AutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationRuleActionExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationRuleActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationRuleActionExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationRuleActionExecutions_AutomationRuleActions_Automa~",
                        column: x => x.AutomationRuleActionId,
                        principalTable: "AutomationRuleActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutomationRuleActionExecutions_AutomationRuleExecutions_Aut~",
                        column: x => x.AutomationRuleExecutionId,
                        principalTable: "AutomationRuleExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId",
                table: "DashboardWidgets",
                column: "DashboardId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleActionExecutions_AutomationRuleActionId",
                table: "AutomationRuleActionExecutions",
                column: "AutomationRuleActionId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleActionExecutions_AutomationRuleExecutionId",
                table: "AutomationRuleActionExecutions",
                column: "AutomationRuleExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleActions_AutomationRuleId",
                table: "AutomationRuleActions",
                column: "AutomationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRuleExecutions_AutomationRuleId",
                table: "AutomationRuleExecutions",
                column: "AutomationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_TriggerClientId",
                table: "AutomationRules",
                column: "TriggerClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_Folders_FolderId",
                table: "FileMetadata",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId",
                principalTable: "Folders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FileMetadata_Folders_FolderId",
                table: "FileMetadata");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders");

            migrationBuilder.DropTable(
                name: "AutomationRuleActionExecutions");

            migrationBuilder.DropTable(
                name: "AutomationRuleActions");

            migrationBuilder.DropTable(
                name: "AutomationRuleExecutions");

            migrationBuilder.DropTable(
                name: "AutomationRules");

            migrationBuilder.DropIndex(
                name: "IX_DashboardWidgets_DashboardId",
                table: "DashboardWidgets");

            migrationBuilder.InsertData(
                table: "AspNetUsers",
                columns: new[] { "Id", "AccessFailedCount", "ConcurrencyStamp", "DateOfBirth", "Email", "EmailConfirmed", "FirstName", "LastName", "LockoutEnabled", "LockoutEnd", "NormalizedEmail", "NormalizedUserName", "PasswordHash", "PhoneNumber", "PhoneNumberConfirmed", "SecurityStamp", "TwoFactorEnabled", "UserName" },
                values: new object[] { "dev-admin-user", 0, "SEED-ADMIN-CONCURRENCY-STAMP", null, "admin@play2day.com", true, "System", "Administrator", false, null, "ADMIN@PLAY2DAY.COM", "ADMIN", "AQAAAAIAAYagAAAAEJ1AhlJOAablYfFpSBJmkOkqLkqidbamfdrRwkTGjXCnkD30AqM6PNAcAh96mQgYXg==", null, false, "SEED-ADMIN-SECURITY-STAMP", false, "admin" });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "CreateDate", "CreatedByUserId", "IsActive", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { "dev-role-admin", "SEED-ROLE-ADMIN-CS", new DateTime(2026, 3, 11, 0, 0, 0, 0, DateTimeKind.Utc), "dev-admin-user", true, "Admin", "ADMIN" },
                    { "dev-role-can-clock-in", "SEED-ROLE-CAN-CLOCK-IN-CS", new DateTime(2026, 3, 11, 0, 0, 0, 0, DateTimeKind.Utc), "dev-admin-user", true, "CanClockIn", "CANCLOCKIN" },
                    { "dev-role-can-control-music", "SEED-ROLE-CAN-CONTROL-MUSIC-CS", new DateTime(2026, 3, 11, 0, 0, 0, 0, DateTimeKind.Utc), "dev-admin-user", true, "CanControlMusic", "CANCONTROLMUSIC" },
                    { "dev-role-manager", "SEED-ROLE-MANAGER-CS", new DateTime(2026, 3, 11, 0, 0, 0, 0, DateTimeKind.Utc), "dev-admin-user", true, "Manager", "MANAGER" },
                    { "dev-role-staff", "SEED-ROLE-STAFF-CS", new DateTime(2026, 3, 11, 0, 0, 0, 0, DateTimeKind.Utc), "dev-admin-user", true, "Staff", "STAFF" }
                });

            migrationBuilder.InsertData(
                table: "AspNetUserRoles",
                columns: new[] { "RoleId", "UserId" },
                values: new object[,]
                {
                    { "dev-role-admin", "dev-admin-user" },
                    { "dev-role-can-clock-in", "dev-admin-user" },
                    { "dev-role-can-control-music", "dev-admin-user" },
                    { "dev-role-manager", "dev-admin-user" },
                    { "dev-role-staff", "dev-admin-user" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_Name",
                table: "Folders",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_FileMetadata_FileName",
                table: "FileMetadata",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId_IsActive_SortOrder",
                table: "DashboardWidgets",
                columns: new[] { "DashboardId", "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_IsActive_Name",
                table: "Dashboards",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_FileMetadata_Folders_FolderId",
                table: "FileMetadata",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}

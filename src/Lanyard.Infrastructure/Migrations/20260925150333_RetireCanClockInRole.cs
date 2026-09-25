using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetireCanClockInRole : Migration
    {
        /// <inheritdoc />
        // CanClockIn gated the old placeholder clock-in page, which the rota's clock-in terminal
        // replaced; nothing checks it any more. Retired the way Manage > Roles deletes a role:
        // everyone is taken out of it and the role is marked inactive, not removed. Matched by
        // name as well as the seeded id in case a database created it differently.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "AspNetUserRoles"
                WHERE "RoleId" IN (SELECT "Id" FROM "AspNetRoles" WHERE "Id" = 'dev-role-can-clock-in' OR "NormalizedName" = 'CANCLOCKIN');

                UPDATE "AspNetRoles" SET "IsActive" = false
                WHERE "Id" = 'dev-role-can-clock-in' OR "NormalizedName" = 'CANCLOCKIN';
                """);
        }

        /// <inheritdoc />
        // Brings the role back, but not who was in it - those links are gone, as they are when a
        // role is deleted from Manage > Roles.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AspNetRoles" SET "IsActive" = true
                WHERE "Id" = 'dev-role-can-clock-in' OR "NormalizedName" = 'CANCLOCKIN';
                """);
        }
    }
}

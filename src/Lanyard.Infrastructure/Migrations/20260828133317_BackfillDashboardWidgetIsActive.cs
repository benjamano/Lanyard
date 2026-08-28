using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDashboardWidgetIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time repair for widgets written before the save path set IsActive. Every widget
            // added in the dashboard editor was persisted inactive, which nothing noticed until the
            // home screen started filtering soft-deleted widgets out. Widgets belonging to a deleted
            // dashboard are left alone so a deleted dashboard stays deleted.
            migrationBuilder.Sql(
                """
                UPDATE "DashboardWidgets" SET "IsActive" = TRUE
                WHERE "DashboardId" IN (SELECT "Id" FROM "Dashboards" WHERE "IsActive");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty - a widget deleted before this migration is indistinguishable in
            // the data from one that was never activated, so the backfill cannot be reversed.
        }
    }
}

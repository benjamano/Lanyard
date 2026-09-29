using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    // A person can't have two live (pending or approved) time-off requests covering the same day.
    // TimeOffService checks this before saving, but a check-then-insert can race (a double submit,
    // or a manager recording the same days at the same moment), so the database enforces it too.
    // EF has no model API for exclusion constraints, hence raw SQL; the model snapshot is unchanged.
    // btree_gist lets the plain-equality "UserId" column share a GiST index with the date range, and
    // is a trusted extension on Postgres 13+, so the database owner can create it.
    /// <inheritdoc />
    public partial class AddTimeOffOverlapConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            // Status 0 = Pending, 1 = Approved (TimeOffStatus).
            migrationBuilder.Sql("""
                ALTER TABLE "TimeOffRequests"
                ADD CONSTRAINT "EX_TimeOffRequests_NoOverlappingLiveRequests"
                EXCLUDE USING gist (
                    "UserId" WITH =,
                    daterange("StartDate", "EndDate", '[]') WITH &&
                )
                WHERE ("Status" IN (0, 1));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The extension is left installed: other objects may have come to depend on it.
            migrationBuilder.Sql("""ALTER TABLE "TimeOffRequests" DROP CONSTRAINT IF EXISTS "EX_TimeOffRequests_NoOverlappingLiveRequests";""");
        }
    }
}

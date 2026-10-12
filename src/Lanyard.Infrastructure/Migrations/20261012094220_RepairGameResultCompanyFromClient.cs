using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only repair. Until GameResultService started copying the kiosk client's company onto
    /// each game, every completed laser game was saved under Play2Day (company 1), because kiosk
    /// hub calls run as the system tenant and ApplyTenantToChanges defaults those inserts to
    /// Play2Day. This moves each game to its client's company.
    /// </summary>
    /// <remarks>
    /// Only rows whose company differs from their client's are touched, so it is safe to re-run
    /// and a no-op on an already-correct database. Note that re-running it after a client has
    /// been moved to another company would also move that client's older games.
    /// </remarks>
    public partial class RepairGameResultCompanyFromClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE
    repaired_count integer;
BEGIN
    UPDATE ""GameResults"" AS g
    SET ""CompanyId"" = c.""CompanyId""
    FROM ""Clients"" AS c
    WHERE g.""ClientId"" = c.""Id""
      AND g.""CompanyId"" <> c.""CompanyId"";

    GET DIAGNOSTICS repaired_count = ROW_COUNT;

    RAISE NOTICE 'RepairGameResultCompanyFromClient: moved % GameResults row(s) to their client''s company', repaired_count;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op. The original values were all the Play2Day fallback, but there
            // is no way to tell a repaired row apart from one that genuinely belongs where it now
            // is, so reverting would put correct rows back into the wrong company. To restore the
            // exact previous values, use the list of affected GameResult IDs captured before the
            // deploy, or a database backup.
        }
    }
}

using System.Globalization;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// The games a game binding looks at: this company's, played since venue-local midnight, at the
/// kiosk's location. GameResult has no location of its own, so it comes from the laser system's
/// client. A client with no location (the screen's or the laser system's) counts as company-wide,
/// so a single-venue company works without assigning locations at all.
/// </summary>
internal static class GameBindingScope
{
    public static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    public static IQueryable<GameResult> GamesToday(ApplicationDbContext ctx, ProjectionBindingContext context)
    {
        // Venue-local, not server-local: the server runs in UTC, so DateTime.Now would start
        // "today" an hour late all summer.
        DateTime sinceUtc = RotaTime.StartOfDayUtc(RotaTime.Today(context.NowUtc));

        IQueryable<GameResult> games = ctx.GameResults
            .Where(g => g.CompanyId == context.CompanyId && g.PlayedAtUtc >= sinceUtc && g.PlayedAtUtc <= context.NowUtc);

        if (context.LocationId is int locationId)
        {
            games = games.Where(g => g.Client == null || g.Client.LocationId == null || g.Client.LocationId == locationId);
        }

        return games;
    }

    public static string PlayerLabel(GameResultPlayerScore score)
    {
        // PlayerName is always null today (the hardware only knows guns), but it's the better label
        // the moment something starts filling it in.
        return string.IsNullOrWhiteSpace(score.PlayerName) ? $"Gun {score.GunId}" : score.PlayerName.Trim();
    }
}

using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// What a location's rota should have for firstDay..to, and what it has. TemplateDemand and
// PartyDemand include shifts that have already started (so a party during a shift that's under way
// isn't reported as uncovered); callers drop those by time. Existing is every active shift that
// could count towards the demand, padded either side so overnight and early shifts are caught.
internal sealed record RotaDemand(
    List<RotaHoliday> Holidays,
    List<PartyStaffingRule> PartyRules,
    List<PartyBooking> Parties,
    List<DemandSlot> TemplateDemand,
    List<DemandSlot> PartyDemand,
    List<PartyBooking> Uncovered,
    List<Shift> Existing,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc);

// The read half of Auto-build, shared with PartyStaffingWatch so "is this day's party staffing
// right?" is answered exactly the way Auto-build would answer it. Every query filters by location,
// so it is safe on the unfiltered system context as well as the tenant one. Reads only.
internal static class RotaDemandLoader
{
    // Active templates with only their active-position slots (an archived position is skipped, not recreated).
    public static async Task<List<ShiftTemplate>> LoadTemplatesAsync(ApplicationDbContext ctx, int locationId, CancellationToken cancellationToken = default)
    {
        List<ShiftTemplate> templates = await ShiftTemplateService.ForLocation(ctx, locationId).ToListAsync(cancellationToken);

        foreach (ShiftTemplate template in templates)
        {
            template.Slots = template.Slots.Where(x => x.StaffPosition is { IsActive: true }).ToList();
        }

        return templates;
    }

    public static async Task<RotaDemand> LoadAsync(ApplicationDbContext ctx, int locationId, IReadOnlyCollection<ShiftTemplate> templates,
        DateOnly firstDay, DateOnly to, CancellationToken cancellationToken = default)
    {
        List<RotaHoliday> holidays = await RotaHolidayService.ForRangeAsync(ctx, locationId, firstDay, to);

        DateTime windowStartUtc = RotaTime.StartOfDayUtc(firstDay);
        DateTime windowEndUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        // Booked parties add their type's staff, when the location uses Parties.
        List<PartyBooking> parties = [];
        List<PartyStaffingRule> partyRules = [];

        if (await CompanyFeatureQueries.IsEnabledForLocationAsync(ctx, locationId, CompanyFeature.Parties, cancellationToken))
        {
            partyRules = (await PartyStaffingService.ForLocation(ctx, locationId).ToListAsync(cancellationToken))
                .Where(x => x.StaffPosition is { IsActive: true })
                .ToList();

            if (partyRules.Count > 0)
            {
                parties = await ctx.PartyBookings
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => x.LocationId == locationId && x.Status == PartyBookingStatus.Booked
                        && x.StartUtc >= windowStartUtc && x.StartUtc < windowEndUtc)
                    .ToListAsync(cancellationToken);
            }
        }

        List<DemandSlot> templateDemand = RotaAutoBuildRules.ExpandTemplates(templates, firstDay, to, holidays);
        (List<DemandSlot> partyDemand, List<PartyBooking> uncovered) = RotaAutoBuildRules.ExpandParties(parties, partyRules, templateDemand);

        // Shifts that could count towards the demand, plus every party shift Auto-build made in
        // the window (whatever happened to the party since).
        List<Shift> existing = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId && x.IsActive
                && x.StartUtc >= windowStartUtc.AddHours(-6) && x.StartUtc < windowEndUtc.AddDays(1))
            .ToListAsync(cancellationToken);

        return new RotaDemand(holidays, partyRules, parties, templateDemand, partyDemand, uncovered, existing, windowStartUtc, windowEndUtc);
    }
}

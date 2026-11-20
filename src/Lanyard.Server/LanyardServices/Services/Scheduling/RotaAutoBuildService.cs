using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class RotaAutoBuildService(
    IDbContextFactory<ApplicationDbContext> factory,
    IContractRequirementService contractRequirementService,
    ILogger<RotaAutoBuildService> logger,
    TimeProvider? timeProvider = null) : IRotaAutoBuildService
{
    // A month view is at most 31 days; a little slack for callers that pad it.
    public const int MaxRangeDays = 42;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IContractRequirementService _contractRequirementService = contractRequirementService;
    private readonly ILogger<RotaAutoBuildService> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<Result<AutoBuildResult>> PreviewAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to) =>
        RunAsync(scope, locationId, from, to, actingUserId: null);

    public Task<Result<AutoBuildResult>> ApplyAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId) =>
        RunAsync(scope, locationId, from, to, actingUserId);

    private async Task<Result<AutoBuildResult>> RunAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string? actingUserId)
    {
        try
        {
            if (to < from)
            {
                return Result<AutoBuildResult>.Fail("The end of the range must not be before the start.");
            }

            if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            {
                return Result<AutoBuildResult>.Fail($"Auto-build works on up to {MaxRangeDays} days at a time.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<AutoBuildResult>.Fail("You can only build the rota for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Company)
                .FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

            if (location is null)
            {
                return Result<AutoBuildResult>.Fail("Location not found.");
            }

            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

            // Days already over are left alone, and so is anything already started today.
            DateOnly today = RotaTime.Today(nowUtc);
            DateOnly firstDay = from < today ? today : from;

            List<ShiftTemplate> templates = await ShiftTemplateService.ForLocation(ctx, locationId).ToListAsync();

            // A template's archived position is skipped rather than recreated.
            foreach (ShiftTemplate template in templates)
            {
                template.Slots = template.Slots.Where(x => x.StaffPosition is { IsActive: true }).ToList();
            }

            if (firstDay > to)
            {
                return Result<AutoBuildResult>.Ok(new AutoBuildResult(location.GetDisplayName(), from, to, templates.Count, 0, []));
            }

            List<RotaHoliday> holidays = await RotaHolidayService.ForRangeAsync(ctx, locationId, firstDay, to);

            DateTime windowStartUtc = RotaTime.StartOfDayUtc(firstDay);
            DateTime windowEndUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            // Booked parties add their type's staff, when the company uses both modules.
            List<PartyBooking> parties = [];
            List<PartyStaffingRule> partyRules = [];

            if (await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, location.CompanyId, CompanyFeature.Parties))
            {
                partyRules = (await PartyStaffingService.ForLocation(ctx, locationId).ToListAsync())
                    .Where(x => x.StaffPosition is { IsActive: true })
                    .ToList();

                if (partyRules.Count > 0)
                {
                    parties = await ctx.PartyBookings
                        .AsNoTracking()
                        .TagWithCallSite()
                        .Where(x => x.LocationId == locationId && x.Status == PartyBookingStatus.Booked
                            && x.StartUtc >= windowStartUtc && x.StartUtc < windowEndUtc)
                        .ToListAsync();
                }
            }

            // Parties are matched to every template shift those days, including any already started,
            // so a party during a shift that's under way isn't reported as having no shift.
            List<DemandSlot> templateDemand = RotaAutoBuildRules.ExpandTemplates(templates, firstDay, to, holidays);
            (List<DemandSlot> partyDemand, List<PartyBooking> uncovered) = RotaAutoBuildRules.ExpandParties(parties, partyRules, templateDemand);

            List<DemandSlot> demand = templateDemand
                .Concat(partyDemand)
                .Where(x => x.StartUtc > nowUtc)
                .ToList();

            // Shifts that could count towards the demand, plus every party shift Auto-build made in
            // the window (whatever happened to the party since).
            List<Shift> existing = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.IsActive
                    && x.StartUtc >= windowStartUtc.AddHours(-6) && x.StartUtc < windowEndUtc.AddDays(1))
                .ToListAsync();

            (List<DemandSlot> remaining, int covered, List<Shift> unmatched) = RotaAutoBuildRules.Match(demand, existing);

            // Party shifts that no longer match their party (cancelled, moved, or its rules changed).
            // Drafts are taken off; a published one stays for the manager to sort out, and still
            // counts towards its party's staff if the party is on, so it isn't doubled up.
            List<Shift> stale = unmatched
                .Where(x => x.PartyBookingId is not null && x.StartUtc > nowUtc && x.StartUtc >= windowStartUtc && x.StartUtc < windowEndUtc)
                .ToList();

            Dictionary<string, string> names = stale.Any(x => x.UserId is not null)
                ? await ctx.Users
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(u => stale.Select(x => x.UserId).Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => RotaNames.For(u))
                : [];

            Dictionary<Guid, string> positionNames = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == location.CompanyId)
                .ToDictionaryAsync(x => x.Id, x => x.Name);

            List<Shift> toRemove = [];
            List<AutoBuildLine> removedLines = [];
            List<string> warnings = [];

            foreach (Shift shift in stale)
            {
                string who = shift.UserId is string id ? names.GetValueOrDefault(id, "Someone") : "Open shift";
                string position = shift.StaffPositionId is Guid p ? positionNames.GetValueOrDefault(p, "Position") : "No position";
                DateOnly day = RotaTime.LocalDate(shift.StartUtc);

                if (shift.PublishedDateUtc is null)
                {
                    toRemove.Add(shift);
                    removedLines.Add(new AutoBuildLine(shift.Notes ?? "Party", day, shift.StartUtc, shift.EndUtc, position,
                        shift.UserId, shift.UserId is null ? null : who, null, "The party was cancelled or changed."));
                    continue;
                }

                int index = remaining.FindIndex(x => x.PartyBookingId == shift.PartyBookingId && x.StaffPositionId == shift.StaffPositionId);

                if (index >= 0)
                {
                    remaining[index] = remaining[index] with { Needed = remaining[index].Needed - 1 };
                    remaining.RemoveAll(x => x.Needed <= 0);
                    warnings.Add($"{who}'s {position} shift on {day.ToString("ddd d MMM", RotaFormat.Uk)} ({RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc)}) no longer matches its party's times. It's published, so change it on the rota.");
                }
                else
                {
                    warnings.Add($"{who}'s {position} shift on {day.ToString("ddd d MMM", RotaFormat.Uk)} ({RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc)}) is for a party that was cancelled or no longer needs it. It's published, so remove it on the rota if it isn't needed.");
                }
            }

            foreach (PartyBooking party in uncovered.Where(x => x.StartUtc > nowUtc))
            {
                DateOnly partyDay = RotaTime.LocalDate(party.StartUtc);

                if (RotaHolidayRules.For(holidays, partyDay).Closed is RotaHoliday closed)
                {
                    warnings.Add($"The {Describe(party)} on {partyDay.ToString("ddd d MMM", RotaFormat.Uk)} ({RotaFormat.TimeRange(party.StartUtc, party.EndUtc)}) "
                        + $"is on a day marked closed ({closed.Name}), so nobody was added for it. Move the booking, or change the day under Holidays.");
                    continue;
                }

                warnings.Add($"No shift template covers the {Describe(party)} on "
                    + $"{RotaTime.LocalDate(party.StartUtc).ToString("ddd d MMM", RotaFormat.Uk)} ({RotaFormat.TimeRange(party.StartUtc, party.EndUtc)}), "
                    + "so nobody was added for it. Add a template for that time, or add its staff by hand.");
            }

            int partyCount = parties.Count(p => demand.Any(d => d.PartyBookingId == p.Id));

            List<string> holidayLines = HolidayLines(holidays, firstDay, to);

            AutoBuildResult Build(List<AutoBuildLine> lines) =>
                new(location.GetDisplayName(), from, to, templates.Count, covered, lines, partyCount, removedLines, warnings, holidayLines);

            if (remaining.Count == 0 && toRemove.Count == 0)
            {
                return Result<AutoBuildResult>.Ok(Build([]));
            }

            SuggestionPool pool = await SuggestionPool.LoadAsync(ctx, _contractRequirementService, _logger, locationId, location.CompanyId, firstDay, to);

            List<AutoBuildWorker> workers = pool.Users.Select(user =>
            {
                StaffPosition? primary = pool.PrimaryPositions.GetValueOrDefault(user.Id);

                Dictionary<DateOnly, string> pending = [];

                for (DateOnly day = firstDay; day <= to; day = day.AddDays(1))
                {
                    if (pool.PendingTimeOffOn(user.Id, day) is string name)
                    {
                        pending[day] = name;
                    }
                }

                return new AutoBuildWorker(user.Id, RotaNames.For(user), primary?.Id, primary?.Name, pool.ContractFor(user.Id),
                    pool.ShiftsByUser[user.Id].ToList(), pool.Availability[user.Id].ToList(), pending,
                    pool.Wishes.GetValueOrDefault(user.Id));
            }).ToList();

            Dictionary<Guid, StaffPosition> positions = templates
                .SelectMany(x => x.Slots)
                .Select(x => x.StaffPosition!)
                .Concat(partyRules.Select(x => x.StaffPosition!))
                .DistinctBy(x => x.Id)
                .ToDictionary(x => x.Id);

            List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(
                locationId,
                remaining,
                workers,
                (worker, shift) => ShiftClaimRules.WhyCantWork(pool.CalendarFor(worker.UserId), shift, null, aboutViewer: false),
                actingUserId ?? string.Empty,
                nowUtc,
                positions);

            AutoBuildResult result = Build(picks.Select(x => x.Line).ToList());

            if (actingUserId is null)
            {
                return Result<AutoBuildResult>.Ok(result);
            }

            foreach (AutoBuildPick pick in picks)
            {
                // The position is only attached for the rules' wording; it must not be re-inserted.
                pick.Shift.StaffPosition = null;
                ctx.Shifts.Add(pick.Shift);
            }

            foreach (Shift shift in toRemove)
            {
                // Never published, so nobody was told about it and there's nothing to tell them.
                ctx.Shifts.Attach(shift);
                shift.IsActive = false;
                shift.RemovalPending = false;
                shift.UpdateDate = nowUtc;
                shift.UpdateByUserId = actingUserId;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Auto-build removed {Removed} draft shifts for changed parties at {LocationId}", toRemove.Count, locationId);
            _logger.LogInformation("Auto-build added {Assigned} assigned and {Open} open draft shifts at {LocationId} for {From} to {To} by {ActingUserId}",
                result.Assigned, result.Open, locationId, from, to, actingUserId);

            return Result<AutoBuildResult>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-build failed at {LocationId} for {From} to {To}", locationId, from, to);
            return Result<AutoBuildResult>.Fail($"Auto-build failed: {ex.Message}");
        }
    }

    // One line per holiday in the range: "Mon 31 Aug: Summer bank holiday - staffed like a Sunday".
    // A school holiday covering several days is given as its dates in the range.
    internal static List<string> HolidayLines(IReadOnlyCollection<RotaHoliday> holidays, DateOnly from, DateOnly to)
    {
        List<string> lines = [];

        foreach (RotaHoliday holiday in holidays.Where(x => x.IsActive && x.StartDate <= to && x.EndDate >= from).OrderBy(x => x.StartDate).ThenBy(x => x.Kind))
        {
            DateOnly start = holiday.StartDate < from ? from : holiday.StartDate;
            DateOnly end = holiday.EndDate > to ? to : holiday.EndDate;
            string dates = start == end
                ? start.ToString("ddd d MMM", RotaFormat.Uk)
                : $"{start.ToString("ddd d MMM", RotaFormat.Uk)} to {end.ToString("ddd d MMM", RotaFormat.Uk)}";

            lines.Add($"{dates}: {holiday.Name} - {RotaHolidayRules.Effect(holiday)}");
        }

        return lines;
    }

    // "Lazer + Play party", "party", "Laser tag + Play private hire".
    private static string Describe(PartyBooking booking) =>
        booking.HireKind is PrivateHireKind kind ? $"{kind.Label()} private hire"
        : PartyStaffingRule.Normalise(booking.PartyType) is string type ? $"{type} party" : "party";
}

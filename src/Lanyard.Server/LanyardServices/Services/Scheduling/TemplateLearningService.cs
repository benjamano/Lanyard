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

public class TemplateLearningService(
    IDbContextFactory<ApplicationDbContext> factory,
    IContractRequirementService contractRequirementService,
    IShiftTemplateService templateService,
    IPartyStaffingService partyStaffingService,
    ILogger<TemplateLearningService> logger,
    TimeProvider? timeProvider = null) : ITemplateLearningService
{
    private const int MaxExamples = 8;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IContractRequirementService _contractRequirementService = contractRequirementService;
    private readonly IShiftTemplateService _templateService = templateService;
    private readonly IPartyStaffingService _partyStaffingService = partyStaffingService;
    private readonly ILogger<TemplateLearningService> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<Result<TemplateLearningResult>> LearnAsync(LocationScope scope, int locationId, int weeks)
    {
        try
        {
            if (weeks < ITemplateLearningService.MinWeeks || weeks > ITemplateLearningService.MaxWeeks)
            {
                return Result<TemplateLearningResult>.Fail($"Learn from between {ITemplateLearningService.MinWeeks} and {ITemplateLearningService.MaxWeeks} weeks.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<TemplateLearningResult>.Fail("You can only learn templates for the location you manage.");
            }

            DateOnly thisWeek = RotaTime.GetWeekStart(RotaTime.Today(_time.GetUtcNow().UtcDateTime));
            DateOnly from = thisWeek.AddDays(-7 * weeks);
            DateOnly to = thisWeek.AddDays(-1);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            return Result<TemplateLearningResult>.Ok(await LearnAsync(ctx, locationId, from, to, withCheck: true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to learn shift templates for {LocationId}", locationId);
            return Result<TemplateLearningResult>.Fail($"Failed to learn from the rota: {ex.Message}");
        }
    }

    // Shared with the monthly suggestions, which have no signed-in user.
    internal async Task<TemplateLearningResult> LearnAsync(ApplicationDbContext ctx, int locationId, DateOnly from, DateOnly to, bool withCheck)
    {
        Location location = await ctx.Locations.AsNoTracking().TagWithCallSite().SingleAsync(x => x.Id == locationId);

        DateTime fromUtc = RotaTime.StartOfDayUtc(from);
        DateTime toUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        // What was actually worked: published shifts still on the rota.
        List<Shift> shifts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId && x.IsActive && x.PublishedDateUtc != null && x.StartUtc >= fromUtc && x.StartUtc < toUtc)
            .ToListAsync();

        List<PartyBooking> bookings = await ctx.PartyBookings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId && x.Status == PartyBookingStatus.Booked && x.StartUtc >= fromUtc.AddDays(-1) && x.StartUtc < toUtc.AddDays(1))
            .ToListAsync();

        List<string> userIds = shifts.Where(x => x.UserId is not null).Select(x => x.UserId!).Distinct().ToList();

        Dictionary<string, string> names = (await ctx.Users
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => userIds.Contains(x.Id))
            .ToListAsync())
            .ToDictionary(x => x.Id, x => RotaNames.For(x));

        Dictionary<Guid, StaffPosition> positions = await ctx.StaffPositions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == location.CompanyId)
            .ToDictionaryAsync(x => x.Id);

        Dictionary<Guid, string> positionNames = positions.ToDictionary(x => x.Key, x => x.Value.Name);

        List<ShiftTemplate> existing = await ShiftTemplateService.ForLocation(ctx, locationId).ToListAsync();
        List<PartyStaffingRule> rules = await PartyStaffingService.ForLocation(ctx, locationId).ToListAsync();

        // Archived positions and people who've left aren't suggested.
        HashSet<string> members = (await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == locationId)
            .Select(x => x.UserId)
            .ToListAsync()).ToHashSet();

        TemplateLearningResult result = TemplateLearningRules.Learn(from, to,
            shifts.Where(x => x.StaffPositionId is Guid p && positions.TryGetValue(p, out StaffPosition? position) && position.IsActive).ToList(),
            bookings, names, positionNames, existing, rules);

        result = result with
        {
            Templates = result.Templates
                .Select(t => t with { Lines = t.Lines.Select(l => l with { Regulars = l.Regulars.Where(r => members.Contains(r.UserId)).ToList() }).ToList() })
                .ToList()
        };

        if (!withCheck || result.Templates.Count == 0)
        {
            return result;
        }

        LearnedCheck check = await CheckAsync(ctx, location, from, to, shifts, bookings, result, positions, rules, names);

        return result with { Check = check };
    }

    // Runs Auto-build over the same weeks with the learned templates and hire staffing (and the
    // location's kids' party staffing), against an empty rota, and compares it with what was worked.
    private async Task<LearnedCheck> CheckAsync(ApplicationDbContext ctx, Location location, DateOnly from, DateOnly to, List<Shift> actual,
        List<PartyBooking> bookings, TemplateLearningResult learned, Dictionary<Guid, StaffPosition> positions,
        List<PartyStaffingRule> rules, Dictionary<string, string> names)
    {
        List<ShiftTemplate> templates = learned.Templates.Select(t =>
        {
            ShiftTemplate template = TemplateLearningRules.ToTemplate(t, location.Id);
            template.Id = Guid.NewGuid();
            template.IsActive = true;

            foreach (ShiftTemplateSlot slot in template.Slots)
            {
                slot.StaffPosition = positions.GetValueOrDefault(slot.StaffPositionId);
            }

            return template;
        }).ToList();

        List<PartyStaffingRule> checkRules =
        [
            .. rules.Where(x => x.HireKind is null),
            .. learned.HireStaffing.Select(x => new PartyStaffingRule
            {
                CompanyId = location.CompanyId,
                LocationId = location.Id,
                HireKind = x.HireKind,
                StaffPositionId = x.StaffPositionId,
                StaffPosition = positions.GetValueOrDefault(x.StaffPositionId),
                StaffCount = x.Count
            })
        ];

        List<DemandSlot> templateDemand = RotaAutoBuildRules.ExpandTemplates(templates, from, to);
        (List<DemandSlot> partyDemand, _) = RotaAutoBuildRules.ExpandParties(bookings, checkRules, templateDemand);

        SuggestionPool pool = await SuggestionPool.LoadAsync(ctx, _contractRequirementService, _logger, location.Id, location.CompanyId, from, to);

        // The rota being compared against isn't there: nobody is "already working" those shifts.
        HashSet<Guid> actualIds = actual.Select(x => x.Id).ToHashSet();

        foreach (ShiftClaimRules.WorkerCalendar calendar in pool.Calendars.Values)
        {
            calendar.Shifts.RemoveAll(x => actualIds.Contains(x.Id));
        }

        List<AutoBuildWorker> workers = pool.Users.Select(user =>
        {
            StaffPosition? primary = pool.PrimaryPositions.GetValueOrDefault(user.Id);
            return new AutoBuildWorker(user.Id, RotaNames.For(user), primary?.Id, primary?.Name, pool.ContractFor(user.Id),
                pool.ShiftsByUser[user.Id].Where(x => !actualIds.Contains(x.Id)).ToList(), pool.Availability[user.Id].ToList(),
                new Dictionary<DateOnly, string>(), pool.Wishes.GetValueOrDefault(user.Id));
        }).ToList();

        List<AutoBuildPick> picks = RotaAutoBuildRules.Assign(location.Id, [.. templateDemand, .. partyDemand], workers,
            (worker, shift) => ShiftClaimRules.WhyCantWork(pool.CalendarFor(worker.UserId), shift, null, aboutViewer: false),
            string.Empty, _time.GetUtcNow().UtcDateTime, positions);

        return Compare(actual.Where(x => x.StaffPositionId is not null).ToList(), picks.Select(x => x.Shift).ToList(), positions, names);
    }

    internal static LearnedCheck Compare(List<Shift> actual, List<Shift> built, IReadOnlyDictionary<Guid, StaffPosition> positions, IReadOnlyDictionary<string, string> names)
    {
        // The same shift: same day and position, starting and ending within 15 minutes.
        static bool Same(Shift a, Shift b) =>
            a.StaffPositionId == b.StaffPositionId
            && RotaTime.LocalDate(a.StartUtc) == RotaTime.LocalDate(b.StartUtc)
            && TemplateLearningRules.Near(RotaTime.LocalTime(a.StartUtc), RotaTime.LocalTime(b.StartUtc))
            && TemplateLearningRules.Near(RotaTime.LocalTime(a.EndUtc), RotaTime.LocalTime(b.EndUtc));

        List<Shift> unused = [.. built];
        List<Shift> ordered = actual.OrderBy(x => x.StartUtc).ToList();
        HashSet<Shift> matched = [];
        int samePerson = 0, sameShift = 0, missed = 0;
        List<string> examples = [];

        // People first, so a right-person match isn't used up as a right-shift one.
        foreach (Shift shift in ordered)
        {
            Shift? match = unused.FirstOrDefault(x => Same(x, shift) && x.UserId == shift.UserId);

            if (match is not null)
            {
                unused.Remove(match);
                matched.Add(shift);
                samePerson++;
            }
        }

        foreach (Shift shift in ordered.Where(x => !matched.Contains(x)))
        {
            Shift? match = unused.FirstOrDefault(x => Same(x, shift));
            string who = shift.UserId is string id ? names.GetValueOrDefault(id, "Someone") : "Open shift";
            string what = $"{RotaTime.LocalDate(shift.StartUtc).ToString("ddd d MMM", RotaFormat.Uk)} {RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc)} {positions.GetValueOrDefault(shift.StaffPositionId!.Value)?.Name ?? "Position"}";

            if (match is not null)
            {
                unused.Remove(match);
                sameShift++;

                if (examples.Count < MaxExamples)
                {
                    string instead = match.UserId is string other ? names.GetValueOrDefault(other, "someone else") : "an open shift";
                    examples.Add($"{what}: {who} worked it, Auto-build would pick {instead}.");
                }

                continue;
            }

            missed++;

            if (examples.Count < MaxExamples)
            {
                examples.Add($"{what} ({who}) isn't in the templates.");
            }
        }

        return new LearnedCheck(actual.Count, samePerson, sameShift, missed, unused.Count, examples);
    }

    public async Task<Result<int>> ApplyAsync(LocationScope scope, int locationId, IReadOnlyCollection<LearnedTemplate> templates,
        IReadOnlyCollection<LearnedHireStaffing> hireStaffing, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<int>.Fail("You can only change shift templates for the location you manage.");
            }

            int saved = 0;
            List<string> failed = [];

            foreach (LearnedTemplate learned in templates.Where(x => x.Status != LearnedStatus.Same))
            {
                Result<ShiftTemplate> result = await _templateService.SaveTemplateAsync(scope, TemplateLearningRules.ToTemplate(learned, locationId), actingUserId);

                if (result.IsSuccess)
                {
                    saved++;
                }
                else
                {
                    failed.Add($"{learned.Name}: {result.Error}");
                }
            }

            if (hireStaffing.Count > 0)
            {
                Result<List<PartyStaffingRule>> existing = await _partyStaffingService.GetRulesAsync(scope, locationId);

                foreach (LearnedHireStaffing line in hireStaffing.Where(x => !x.AlreadySet))
                {
                    PartyStaffingRule? current = existing.Data?.FirstOrDefault(x => x.HireKind == line.HireKind && x.StaffPositionId == line.StaffPositionId);

                    Result<PartyStaffingRule> result = await _partyStaffingService.SaveRuleAsync(scope, new PartyStaffingRule
                    {
                        Id = current?.Id ?? Guid.Empty,
                        CompanyId = 0,
                        LocationId = locationId,
                        HireKind = line.HireKind,
                        StaffPositionId = line.StaffPositionId,
                        StaffCount = line.Count
                    }, actingUserId);

                    if (result.IsSuccess)
                    {
                        saved++;
                    }
                    else
                    {
                        failed.Add($"{line.HireKind.Label()} hire {line.PositionName}: {result.Error}");
                    }
                }
            }

            _logger.LogInformation("Learned templates applied at {LocationId}: {Saved} saved, {Failed} failed, by {ActingUserId}", locationId, saved, failed.Count, actingUserId);

            return failed.Count == 0
                ? Result<int>.Ok(saved)
                : Result<int>.Fail($"Saved {saved}, but these couldn't be: {string.Join("; ", failed)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply learned templates at {LocationId}", locationId);
            return Result<int>.Fail($"Failed to save the templates: {ex.Message}");
        }
    }

    public async Task<Result<TemplateSuggestionRun?>> GetOpenSuggestionsAsync(LocationScope scope, int locationId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<TemplateSuggestionRun?>.Ok(null);
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TemplateSuggestionRun? latest = await ctx.TemplateSuggestionRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId)
                .OrderByDescending(x => x.RunUtc)
                .FirstOrDefaultAsync();

            return Result<TemplateSuggestionRun?>.Ok(latest is { IsOpen: true } ? latest : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load template suggestions for {LocationId}", locationId);
            return Result<TemplateSuggestionRun?>.Fail($"Failed to load template suggestions: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DismissSuggestionsAsync(LocationScope scope, int locationId, string actingUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<bool>.Fail("You can only change shift templates for the location you manage.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime now = _time.GetUtcNow().UtcDateTime;
            List<TemplateSuggestionRun> open = await ctx.TemplateSuggestionRuns
                .Where(x => x.LocationId == locationId && x.DismissedUtc == null)
                .ToListAsync();

            foreach (TemplateSuggestionRun run in open)
            {
                run.DismissedUtc = now;
                run.DismissedByUserId = actingUserId;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Template suggestions for {LocationId} dismissed by {ActingUserId}", locationId, actingUserId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dismiss template suggestions for {LocationId}", locationId);
            return Result<bool>.Fail($"Failed to dismiss the suggestions: {ex.Message}");
        }
    }

    // The monthly run for one location (no signed-in user): learns from the last MonthlyWeeks whole
    // weeks and records how many changes it would suggest. A location with templates gets suggested
    // changes; one without gets its first templates suggested, once at least FirstSetupWeeks of
    // those weeks have a published rota - with the check, so managers see how well they'd work.
    // Returns null when there's nothing to look at yet.
    public async Task<TemplateSuggestionRun?> RunMonthlyAsync(ApplicationDbContext ctx, Location location, DateTime nowUtc)
    {
        bool usesTemplates = await ctx.ShiftTemplates
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.LocationId == location.Id && x.IsActive);

        DateOnly thisWeek = RotaTime.GetWeekStart(RotaTime.Today(nowUtc));
        DateOnly from = thisWeek.AddDays(-7 * ITemplateLearningService.MonthlyWeeks);
        DateOnly to = thisWeek.AddDays(-1);

        if (!usesTemplates)
        {
            DateTime fromUtc = RotaTime.StartOfDayUtc(from);
            DateTime toUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            List<DateTime> starts = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == location.Id && x.IsActive && x.PublishedDateUtc != null && x.StartUtc >= fromUtc && x.StartUtc < toUtc)
                .Select(x => x.StartUtc)
                .ToListAsync();

            int weeksWithRota = starts.Select(x => RotaTime.GetWeekStart(RotaTime.LocalDate(x))).Distinct().Count();

            if (weeksWithRota < ITemplateLearningService.FirstSetupWeeks)
            {
                return null;
            }
        }

        TemplateLearningResult result = await LearnAsync(ctx, location.Id, from, to, withCheck: !usesTemplates);

        if (!usesTemplates && result.Templates.Count == 0)
        {
            return null;
        }

        TemplateSuggestionRun run = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = location.CompanyId,
            LocationId = location.Id,
            RunUtc = nowUtc,
            From = from,
            To = to,
            Suggestions = result.Suggestions,
            FirstSetup = !usesTemplates,
            MatchPercent = result.Check?.MatchPercent
        };

        ctx.TemplateSuggestionRuns.Add(run);
        await ctx.SaveChangesAsync();

        return run;
    }
}

using Lanyard.Application.Services.Locations;
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

            if (firstDay > to || templates.Count == 0)
            {
                return Result<AutoBuildResult>.Ok(new AutoBuildResult(location.GetDisplayName(), from, to, templates.Count, 0, []));
            }

            List<DemandSlot> demand = RotaAutoBuildRules.ExpandTemplates(templates, firstDay, to)
                .Where(x => x.StartUtc > nowUtc)
                .ToList();

            DateTime windowStartUtc = RotaTime.StartOfDayUtc(firstDay);
            DateTime windowEndUtc = RotaTime.StartOfDayUtc(to.AddDays(2));

            List<Shift> existing = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.IsActive && x.StartUtc >= windowStartUtc && x.StartUtc < windowEndUtc)
                .ToListAsync();

            (List<DemandSlot> remaining, int covered) = RotaAutoBuildRules.SubtractCovered(demand, existing);

            if (remaining.Count == 0)
            {
                return Result<AutoBuildResult>.Ok(new AutoBuildResult(location.GetDisplayName(), from, to, templates.Count, covered, []));
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

            AutoBuildResult result = new(location.GetDisplayName(), from, to, templates.Count, covered, picks.Select(x => x.Line).ToList());

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

            await ctx.SaveChangesAsync();

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
}

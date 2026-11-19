using Lanyard.Application.Services.Features;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Checklists;

/// <summary>
/// The day-to-day side of checklists, shared by the phone and manager pages, the clock-in terminal and
/// the missed-checklist sweep: making a location's runs for a venue day, working out who may tick, and
/// reading a day back as views. Callers own access checks; everything here names its location, so it
/// is safe on the unfiltered (system) context the terminal and sweep use.
/// </summary>
internal static class ChecklistRunStore
{
    // Whoever is looking at or ticking a checklist, and what decides whether they may.
    internal sealed record Viewer(string UserId, bool IsManagerAtLocation, bool IsOnRota, IReadOnlySet<Guid> Positions);

    public static async Task<bool> FeaturesOnAsync(ApplicationDbContext ctx, int companyId, CancellationToken cancellationToken = default) =>
        await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.Checklists, cancellationToken)
        && await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.StaffScheduling, cancellationToken);

    /// <summary>
    /// Makes the runs a location needs on a venue day: one per active checklist that applies that day,
    /// from the day it was created, never for a future day. Today's run is brought up to date with the
    /// checklist while nobody has ticked anything on it, so a manager's edit before opening counts.
    /// Two callers racing to make the same run is fine: the unique index lets one win and the other
    /// carries on with the winner's run.
    /// </summary>
    public static async Task EnsureRunsAsync(ApplicationDbContext ctx, Location location, DateOnly day, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        DateOnly today = RotaTime.Today(nowUtc);

        if (day > today)
        {
            return;
        }

        List<Checklist> checklists = await ctx.Checklists
            .TagWithCallSite()
            .Include(x => x.Tasks.Where(t => t.IsActive))
            .Where(x => x.LocationId == location.Id && x.IsActive)
            .ToListAsync(cancellationToken);

        if (checklists.Count == 0)
        {
            return;
        }

        List<LocationOpeningHours> hours = await ctx.LocationOpeningHours
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.LocationId == location.Id && x.IsActive)
            .ToListAsync(cancellationToken);

        RotaHolidayDay holiday = RotaHolidayRules.For(await RotaHolidayService.ForRangeAsync(ctx, location.Id, day, day), day);

        List<ChecklistRun> existing = await ctx.ChecklistRuns
            .TagWithCallSite()
            .Include(x => x.Items)
            .ThenInclude(x => x.Completions)
            .Where(x => x.LocationId == location.Id && x.VenueDate == day)
            .ToListAsync(cancellationToken);

        bool changed = false;

        foreach (Checklist checklist in checklists)
        {
            if (!ChecklistRules.AppliesOn(checklist, day, hours, holiday) || day < RotaTime.LocalDate(checklist.CreateDate))
            {
                continue;
            }

            List<ChecklistTask> tasks = [.. checklist.Tasks.OrderBy(x => x.SortOrder).ThenBy(x => x.CreateDate)];
            DateTime? due = ChecklistRules.DueUtc(checklist, day, hours);
            ChecklistRun? run = existing.FirstOrDefault(x => x.ChecklistId == checklist.Id);

            if (run is null)
            {
                run = new ChecklistRun
                {
                    CompanyId = location.CompanyId,
                    LocationId = location.Id,
                    ChecklistId = checklist.Id,
                    VenueDate = day,
                    Name = checklist.Name,
                    DueUtc = due,
                    CreateDate = nowUtc,
                    Items = [.. tasks.Select((t, i) => Copy(t, i))]
                };

                ctx.ChecklistRuns.Add(run);
                changed = true;
                continue;
            }

            bool untouched = run.Items.All(x => x.Completions.Count == 0);

            if (day == today && untouched && Differs(run, checklist, tasks, due))
            {
                ctx.ChecklistRunItems.RemoveRange(run.Items);
                run.Name = checklist.Name;
                run.DueUtc = due;
                run.Items = [.. tasks.Select((t, i) => Copy(t, i))];
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        try
        {
            await ctx.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone else made the same run a moment ago; theirs stands.
            ctx.ChangeTracker.Clear();
        }
    }

    private static ChecklistRunItem Copy(ChecklistTask task, int sortOrder) => new()
    {
        RunId = Guid.Empty,
        TaskId = task.Id,
        Title = task.Title,
        Detail = task.Detail,
        Response = task.Response,
        Unit = task.Unit,
        SortOrder = sortOrder
    };

    private static bool Differs(ChecklistRun run, Checklist checklist, List<ChecklistTask> tasks, DateTime? due)
    {
        if (run.Name != checklist.Name || run.DueUtc != due || run.Items.Count != tasks.Count)
        {
            return true;
        }

        List<ChecklistRunItem> items = [.. run.Items.OrderBy(x => x.SortOrder)];

        for (int i = 0; i < tasks.Count; i++)
        {
            ChecklistRunItem item = items[i];
            ChecklistTask task = tasks[i];

            if (item.TaskId != task.Id || item.Title != task.Title || item.Detail != task.Detail || item.Response != task.Response || item.Unit != task.Unit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Who someone is for a location's checklists on a venue day. On the rota means a published shift
    /// there starting that day, or being clocked in there now.
    /// </summary>
    public static async Task<Viewer> ViewerAsync(ApplicationDbContext ctx, string userId, int locationId, DateOnly day, bool isManagerAtLocation,
        CancellationToken cancellationToken = default)
    {
        DateTime start = RotaTime.StartOfDayUtc(day);
        DateTime end = RotaTime.StartOfDayUtc(day.AddDays(1));

        bool onRota = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.IsActive && x.PublishedDateUtc != null && x.UserId == userId && x.LocationId == locationId
                    && x.StartUtc >= start && x.StartUtc < end, cancellationToken)
            || await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.IsActive && x.UserId == userId && x.LocationId == locationId && x.ClockOutUtc == null, cancellationToken);

        HashSet<Guid> positions = [.. await ctx.UserPositions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.UserId == userId)
            .Select(x => x.StaffPositionId)
            .ToListAsync(cancellationToken)];

        return new Viewer(userId, isManagerAtLocation, onRota, positions);
    }

    /// <summary>
    /// A location's runs on a venue day as views. staffView leaves out archived checklists; the
    /// manager's board keeps them when anything was ticked, so history isn't hidden. CanTick is for the
    /// viewer, and only ever true on today's runs.
    /// </summary>
    public static async Task<List<ChecklistRunView>> LoadRunsAsync(ApplicationDbContext ctx, int locationId, DateOnly day, DateTime nowUtc,
        Viewer? viewer, bool staffView, CancellationToken cancellationToken = default)
    {
        List<ChecklistRun> runs = await ctx.ChecklistRuns
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.Checklist)
            .ThenInclude(x => x!.StaffPosition)
            .Include(x => x.Items)
            .ThenInclude(x => x.Completions.Where(c => c.IsActive))
            .ThenInclude(x => x.User)
            .AsSplitQuery()
            .Where(x => x.LocationId == locationId && x.VenueDate == day)
            .ToListAsync(cancellationToken);

        bool isToday = day == RotaTime.Today(nowUtc);
        List<ChecklistRunView> views = [];

        foreach (ChecklistRun run in runs
                     .OrderBy(x => x.Checklist?.SortOrder ?? int.MaxValue)
                     .ThenBy(x => x.Name))
        {
            Checklist? checklist = run.Checklist;
            bool anyTicks = run.Items.Any(x => x.Completions.Count > 0);

            if (checklist?.IsActive != true && (staffView || !anyTicks))
            {
                continue;
            }

            List<ChecklistItemView> items = [.. run.Items
                .OrderBy(x => x.SortOrder)
                .Select(x => new ChecklistItemView(x.Id, x.Title, x.Detail, x.Response, x.Unit, Tick(x.Completions.FirstOrDefault())))];

            int done = items.Count(x => x.Tick is not null);

            string? reason = !isToday
                ? "Only today's checklists can be ticked."
                : viewer is null
                    ? "Sign in to tick tasks."
                    : ChecklistRules.WhyCantTick(viewer.IsManagerAtLocation, viewer.IsOnRota,
                        checklist?.StaffPositionId is Guid p && viewer.Positions.Contains(p),
                        checklist?.StaffPositionId, checklist?.StaffPosition?.Name, run.Name);

            views.Add(new ChecklistRunView(run.Id, run.ChecklistId, run.Name, checklist?.Description, run.VenueDate, run.DueUtc,
                ChecklistRules.Status(done, items.Count, run.DueUtc, run.VenueDate, nowUtc),
                done, items.Count, reason is null, reason, items));
        }

        return views;
    }

    private static ChecklistTickView? Tick(ChecklistCompletion? completion) => completion is null
        ? null
        : new ChecklistTickView(completion.Id, completion.UserId, RotaNames.Public(completion.User), completion.CompletedUtc,
            completion.Method, completion.Note, completion.Reading);
}

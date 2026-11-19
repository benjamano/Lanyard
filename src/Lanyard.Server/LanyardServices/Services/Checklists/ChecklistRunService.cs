using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Checklists;

public class ChecklistRunService(
    IDbContextFactory<ApplicationDbContext> factory,
    IClockInTerminalService terminalService,
    ITimeEntryService timeEntryService,
    ITerminalEphemeralTokenService tokenService,
    TimeProvider timeProvider,
    ILogger<ChecklistRunService> logger) : IChecklistRunService
{
    public const int MaxHistoryDays = 62;

    private const string FeatureOff = "Checklists aren't switched on for this company.";
    private const string GrantLapsed = "Enter your PIN again to keep ticking.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IClockInTerminalService _terminalService = terminalService;
    private readonly ITimeEntryService _timeEntryService = timeEntryService;
    private readonly ITerminalEphemeralTokenService _tokenService = tokenService;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChecklistRunService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsManagerAt(LocationScope scope, int locationId) =>
        scope.IsAdmin || (scope.IsManager && scope.LocationId == locationId);

    public async Task<Result<ChecklistDayView>> GetMyTodayAsync(string userId, LocationScope scope)
    {
        try
        {
            DateTime now = Now;
            DateOnly today = RotaTime.Today(now);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Where they're working: clocked in somewhere beats the location they signed in to.
            int? locationId = await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.UserId == userId && x.ClockOutUtc == null)
                .OrderByDescending(x => x.ClockInUtc)
                .Select(x => (int?)x.LocationId)
                .FirstOrDefaultAsync() ?? scope.LocationId;

            if (locationId is not int id)
            {
                return Result<ChecklistDayView>.Fail("Sign in to a location to see its checklists.");
            }

            Result<Location> location = await LocationForStaffAsync(ctx, userId, scope, id);

            if (!location.IsSuccess || location.Data is null)
            {
                return Result<ChecklistDayView>.Fail(location.Error ?? "Location not found.");
            }

            await ChecklistRunStore.EnsureRunsAsync(ctx, location.Data, today, now);

            ChecklistRunStore.Viewer viewer = await ChecklistRunStore.ViewerAsync(ctx, userId, id, today, IsManagerAt(scope, id));
            List<ChecklistRunView> runs = await ChecklistRunStore.LoadRunsAsync(ctx, id, today, now, viewer, staffView: true);

            return Result<ChecklistDayView>.Ok(new ChecklistDayView(id, location.Data.GetDisplayName(), today, runs));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load today's checklists for {UserId}", userId);
            return Result<ChecklistDayView>.Fail($"Failed to load today's checklists: {ex.Message}");
        }
    }

    public async Task<Result<bool>> CompleteAsync(string userId, LocationScope scope, Guid runItemId, string? note, decimal? reading)
    {
        try
        {
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChecklistRunItem? item = await LoadItemAsync(ctx, runItemId);

            if (item?.Run is null)
            {
                return Result<bool>.Fail("That task wasn't found.");
            }

            Result<Location> location = await LocationForStaffAsync(ctx, userId, scope, item.Run.LocationId);

            if (!location.IsSuccess)
            {
                return Result<bool>.Fail(location.Error ?? "That task wasn't found.");
            }

            return await TickAsync(ctx, item, userId, IsManagerAt(scope, item.Run.LocationId), ChecklistCompletionMethod.Phone, null, note, reading, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to tick checklist task {RunItemId}", runItemId);
            return Result<bool>.Fail($"Failed to tick the task: {ex.Message}");
        }
    }

    private static Task<ChecklistRunItem?> LoadItemAsync(ApplicationDbContext ctx, Guid runItemId) =>
        ctx.ChecklistRunItems
            .TagWithCallSite()
            .Include(x => x.Run)
            .ThenInclude(x => x!.Checklist)
            .ThenInclude(x => x!.StaffPosition)
            .Include(x => x.Completions.Where(c => c.IsActive))
            .ThenInclude(x => x.User)
            .FirstOrDefaultAsync(x => x.Id == runItemId);

    // A tick from any path (phone, manager, terminal), once the caller has checked the person may use
    // the location: only today's tasks, one live tick per task, the eligibility rule, and the
    // answer the task asks for.
    private async Task<Result<bool>> TickAsync(ApplicationDbContext ctx, ChecklistRunItem item, string userId, bool isManagerAtLocation,
        ChecklistCompletionMethod method, Guid? terminalId, string? note, decimal? reading, DateTime now)
    {
        ChecklistRun run = item.Run!;

        if (run.VenueDate != RotaTime.Today(now))
        {
            return Result<bool>.Fail("Only today's checklists can be ticked.");
        }

        if (item.Completions.FirstOrDefault() is ChecklistCompletion already)
        {
            return Result<bool>.Fail($"{RotaNames.Public(already.User)} ticked this at {RotaTime.ToVenueLocal(already.CompletedUtc).ToString("HH:mm", RotaFormat.Uk)}.");
        }

        ChecklistRunStore.Viewer viewer = await ChecklistRunStore.ViewerAsync(ctx, userId, run.LocationId, run.VenueDate, isManagerAtLocation);
        Checklist? checklist = run.Checklist;

        if (ChecklistRules.WhyCantTick(viewer.IsManagerAtLocation, viewer.IsOnRota,
                checklist?.StaffPositionId is Guid p && viewer.Positions.Contains(p),
                checklist?.StaffPositionId, checklist?.StaffPosition?.Name, run.Name) is string reason)
        {
            return Result<bool>.Fail(reason);
        }

        Result<(string? Note, decimal? Reading)> answer = Answer(item.Response, note, reading);

        if (!answer.IsSuccess)
        {
            return Result<bool>.Fail(answer.Error!);
        }

        ctx.ChecklistCompletions.Add(new ChecklistCompletion
        {
            RunItemId = item.Id,
            UserId = userId,
            CompletedUtc = now,
            Method = method,
            ClockInTerminalId = terminalId,
            Note = answer.Data.Note,
            Reading = answer.Data.Reading
        });

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Result<bool>.Fail("Someone else ticked this a moment ago.");
        }

        _logger.LogInformation("Checklist task {RunItemId} ticked by {UserId} ({Method})", item.Id, userId, method);

        return Result<bool>.Ok(true);
    }

    // What a tick keeps for its task's response: a number task needs a valid reading; only a
    // tick-with-note task keeps a note. Shared with the terminal.
    internal static Result<(string? Note, decimal? Reading)> Answer(ChecklistResponse response, string? note, decimal? reading)
    {
        string? trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        switch (response)
        {
            case ChecklistResponse.Number:
                if (reading is not decimal value)
                {
                    return Result<(string?, decimal?)>.Fail("Enter the reading before ticking it.");
                }

                if (!ChecklistRules.IsValidReading(value))
                {
                    return Result<(string?, decimal?)>.Fail("That reading isn't a number Lanyard can store. Use at most two decimal places.");
                }

                return Result<(string?, decimal?)>.Ok((null, value));

            case ChecklistResponse.TickWithNote:
                if (trimmed?.Length > ChecklistRules.MaxNoteLength)
                {
                    return Result<(string?, decimal?)>.Fail($"The note can be at most {ChecklistRules.MaxNoteLength} characters.");
                }

                return Result<(string?, decimal?)>.Ok((trimmed, null));

            default:
                return Result<(string?, decimal?)>.Ok((null, null));
        }
    }

    public async Task<Result<bool>> UndoAsync(string userId, LocationScope scope, Guid completionId)
    {
        try
        {
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            ChecklistCompletion? completion = await ctx.ChecklistCompletions
                .TagWithCallSite()
                .Include(x => x.RunItem)
                .ThenInclude(x => x!.Run)
                .FirstOrDefaultAsync(x => x.Id == completionId && x.IsActive);

            ChecklistRun? run = completion?.RunItem?.Run;

            if (completion is null || run is null)
            {
                return Result<bool>.Fail("That tick wasn't found.");
            }

            bool isManager = IsManagerAt(scope, run.LocationId);

            if (completion.UserId != userId && !isManager)
            {
                return Result<bool>.Fail("Only the person who ticked it, or a manager, can take it back.");
            }

            if (!isManager)
            {
                Result<Location> location = await LocationForStaffAsync(ctx, userId, scope, run.LocationId);

                if (!location.IsSuccess)
                {
                    return Result<bool>.Fail(location.Error ?? "That tick wasn't found.");
                }
            }

            if (run.VenueDate != RotaTime.Today(now))
            {
                return Result<bool>.Fail("Only today's ticks can be taken back.");
            }

            completion.IsActive = false;
            completion.UndoneUtc = now;
            completion.UndoneByUserId = userId;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Checklist tick {CompletionId} taken back by {UserId}", completionId, userId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to take back checklist tick {CompletionId}", completionId);
            return Result<bool>.Fail($"Failed to take the tick back: {ex.Message}");
        }
    }

    public async Task<Result<ChecklistDayView>> GetDayBoardAsync(LocationScope scope, string userId, int locationId, DateOnly day)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<ChecklistDayView>.Fail("You can only see checklists for the location you manage.");
            }

            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.AsNoTracking().TagWithCallSite().Include(x => x.Company).FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

            if (location is null)
            {
                return Result<ChecklistDayView>.Fail("Location not found.");
            }

            await ChecklistRunStore.EnsureRunsAsync(ctx, location, day, now);

            ChecklistRunStore.Viewer viewer = new(userId, true, true, new HashSet<Guid>());
            List<ChecklistRunView> runs = await ChecklistRunStore.LoadRunsAsync(ctx, locationId, day, now, viewer, staffView: false);

            return Result<ChecklistDayView>.Ok(new ChecklistDayView(locationId, location.GetDisplayName(), day, runs));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the checklist board for {LocationId} on {Day}", locationId, day);
            return Result<ChecklistDayView>.Fail($"Failed to load the checklists: {ex.Message}");
        }
    }

    public async Task<Result<List<ChecklistHistoryRow>>> GetHistoryAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<List<ChecklistHistoryRow>>.Fail("You can only see checklists for the location you manage.");
            }

            DateTime now = Now;
            DateOnly today = RotaTime.Today(now);

            if (to > today)
            {
                to = today;
            }

            if (from > to)
            {
                return Result<List<ChecklistHistoryRow>>.Fail("Choose a start date on or before the end date.");
            }

            if (to.DayNumber - from.DayNumber + 1 > MaxHistoryDays)
            {
                return Result<List<ChecklistHistoryRow>>.Fail($"Choose at most {MaxHistoryDays} days.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.AsNoTracking().TagWithCallSite().Include(x => x.Company).FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

            if (location is null)
            {
                return Result<List<ChecklistHistoryRow>>.Fail("Location not found.");
            }

            // Days nobody opened still need their runs, so they show as missed.
            for (DateOnly day = from; day <= to; day = day.AddDays(1))
            {
                await ChecklistRunStore.EnsureRunsAsync(ctx, location, day, now);
            }

            var runs = await ctx.ChecklistRuns
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.VenueDate >= from && x.VenueDate <= to)
                .Select(x => new
                {
                    x.Id,
                    x.VenueDate,
                    x.Name,
                    x.DueUtc,
                    Total = x.Items.Count,
                    Done = x.Items.Count(i => i.Completions.Any(c => c.IsActive)),
                    LastTick = x.Items.SelectMany(i => i.Completions).Where(c => c.IsActive).Max(c => (DateTime?)c.CompletedUtc)
                })
                .ToListAsync();

            List<ChecklistHistoryRow> rows = [.. runs
                .Select(x => new ChecklistHistoryRow(x.Id, x.VenueDate, x.Name,
                    ChecklistRules.Status(x.Done, x.Total, x.DueUtc, x.VenueDate, now), x.Done, x.Total, x.DueUtc, x.LastTick))
                .OrderByDescending(x => x.VenueDate)
                .ThenBy(x => x.Name)];

            return Result<List<ChecklistHistoryRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load checklist history for {LocationId}", locationId);
            return Result<List<ChecklistHistoryRow>>.Fail($"Failed to load the checklist history: {ex.Message}");
        }
    }

    public async Task<Result<ChecklistDayView>> GetTerminalDayAsync(Guid terminalId, string? grantToken)
    {
        try
        {
            DateTime now = Now;
            DateOnly today = RotaTime.Today(now);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Result<(TerminalSession Session, Location Location)> terminal = await TerminalLocationAsync(ctx, terminalId);

            if (!terminal.IsSuccess)
            {
                return Result<ChecklistDayView>.Fail(terminal.Error!);
            }

            (TerminalSession session, Location location) = terminal.Data;

            await ChecklistRunStore.EnsureRunsAsync(ctx, location, today, now);

            ChecklistRunStore.Viewer? viewer = null;

            if (_tokenService.TouchChecklistGrant(grantToken, terminalId) is string userId)
            {
                bool isManager = (await SchedulingRecipients.ManagersOfLocationAsync(ctx, session.LocationId)).Contains(userId);
                viewer = await ChecklistRunStore.ViewerAsync(ctx, userId, session.LocationId, today, isManager);
            }

            List<ChecklistRunView> runs = await ChecklistRunStore.LoadRunsAsync(ctx, session.LocationId, today, now, viewer, staffView: true);

            return Result<ChecklistDayView>.Ok(new ChecklistDayView(session.LocationId, session.LocationName, today, runs));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load checklists for terminal {TerminalId}", terminalId);
            return Result<ChecklistDayView>.Fail($"Failed to load the checklists: {ex.Message}");
        }
    }

    public async Task<Result<List<TerminalChecklistPerson>>> GetTerminalPeopleAsync(Guid terminalId)
    {
        try
        {
            Result<List<TerminalRosterEntry>> roster = await _timeEntryService.GetTerminalRosterAsync(terminalId);

            if (!roster.IsSuccess || roster.Data is null)
            {
                return Result<List<TerminalChecklistPerson>>.Fail(roster.Error ?? "This device has been unpaired.");
            }

            Result<TerminalSession> session = await _terminalService.GetActiveSessionAsync(terminalId);

            if (!session.IsSuccess || session.Data is null)
            {
                return Result<List<TerminalChecklistPerson>>.Fail(session.Error ?? "This device has been unpaired.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<TerminalChecklistPerson> people = [.. roster.Data.Select(x => new TerminalChecklistPerson(x.UserId, x.DisplayName, x.HasPin))];
            HashSet<string> already = [.. people.Select(x => x.UserId)];

            List<string> managerIds = [.. (await SchedulingRecipients.ManagersOfLocationAsync(ctx, session.Data.LocationId)).Where(x => !already.Contains(x))];

            if (managerIds.Count > 0)
            {
                List<UserProfile> managers = await ctx.Users
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => managerIds.Contains(x.Id) && (x.LockoutEnd == null || x.LockoutEnd < DateTimeOffset.UtcNow))
                    .ToListAsync();

                HashSet<string> withPin = [.. await ctx.UserClockInPins
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => managerIds.Contains(x.UserId))
                    .Select(x => x.UserId)
                    .ToListAsync()];

                people.AddRange(managers.Select(x => new TerminalChecklistPerson(x.Id, RotaNames.Public(x), withPin.Contains(x.Id))));
            }

            return Result<List<TerminalChecklistPerson>>.Ok([.. people.OrderBy(x => x.DisplayName)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load who can tick checklists at terminal {TerminalId}", terminalId);
            return Result<List<TerminalChecklistPerson>>.Fail($"Failed to load the names: {ex.Message}");
        }
    }

    public async Task<Result<string>> StartTerminalTickingAsync(Guid terminalId, string userId, string pin)
    {
        Result<TerminalSession> verified = await _timeEntryService.VerifyTerminalPinAsync(terminalId, userId, pin);

        if (!verified.IsSuccess)
        {
            return Result<string>.Fail(verified.Error ?? "Couldn't check your PIN.");
        }

        _logger.LogInformation("{UserId} started ticking checklists at terminal {TerminalId}", userId, terminalId);

        return Result<string>.Ok(_tokenService.IssueChecklistGrant(terminalId, userId));
    }

    public async Task<Result<bool>> CompleteOnTerminalAsync(Guid terminalId, string? grantToken, Guid runItemId, string? note, decimal? reading)
    {
        try
        {
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Result<(TerminalSession Session, Location Location)> terminal = await TerminalLocationAsync(ctx, terminalId);

            if (!terminal.IsSuccess)
            {
                return Result<bool>.Fail(terminal.Error!);
            }

            if (_tokenService.TouchChecklistGrant(grantToken, terminalId) is not string userId)
            {
                return Result<bool>.Fail(GrantLapsed);
            }

            int locationId = terminal.Data.Session.LocationId;
            ChecklistRunItem? item = await LoadItemAsync(ctx, runItemId);

            // Only the terminal's own location: an item id from anywhere else is "not found".
            if (item?.Run is null || item.Run.LocationId != locationId)
            {
                return Result<bool>.Fail("That task wasn't found.");
            }

            bool isManager = (await SchedulingRecipients.ManagersOfLocationAsync(ctx, locationId)).Contains(userId);

            return await TickAsync(ctx, item, userId, isManager, ChecklistCompletionMethod.Terminal, terminalId, note, reading, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to tick checklist task {RunItemId} at terminal {TerminalId}", runItemId, terminalId);
            return Result<bool>.Fail($"Failed to tick the task: {ex.Message}");
        }
    }

    public async Task<Result<bool>> UndoOnTerminalAsync(Guid terminalId, string? grantToken, Guid completionId)
    {
        try
        {
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Result<(TerminalSession Session, Location Location)> terminal = await TerminalLocationAsync(ctx, terminalId);

            if (!terminal.IsSuccess)
            {
                return Result<bool>.Fail(terminal.Error!);
            }

            if (_tokenService.TouchChecklistGrant(grantToken, terminalId) is not string userId)
            {
                return Result<bool>.Fail(GrantLapsed);
            }

            int locationId = terminal.Data.Session.LocationId;

            ChecklistCompletion? completion = await ctx.ChecklistCompletions
                .TagWithCallSite()
                .Include(x => x.RunItem)
                .ThenInclude(x => x!.Run)
                .FirstOrDefaultAsync(x => x.Id == completionId && x.IsActive);

            ChecklistRun? run = completion?.RunItem?.Run;

            if (completion is null || run is null || run.LocationId != locationId)
            {
                return Result<bool>.Fail("That tick wasn't found.");
            }

            if (completion.UserId != userId && !(await SchedulingRecipients.ManagersOfLocationAsync(ctx, locationId)).Contains(userId))
            {
                return Result<bool>.Fail("Only the person who ticked it, or a manager, can take it back.");
            }

            if (run.VenueDate != RotaTime.Today(now))
            {
                return Result<bool>.Fail("Only today's ticks can be taken back.");
            }

            completion.IsActive = false;
            completion.UndoneUtc = now;
            completion.UndoneByUserId = userId;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to take back checklist tick {CompletionId} at terminal {TerminalId}", completionId, terminalId);
            return Result<bool>.Fail($"Failed to take the tick back: {ex.Message}");
        }
    }

    public void EndTerminalTicking(string? grantToken) => _tokenService.RevokeChecklistGrant(grantToken);

    // The terminal's live session and its location, with checklists switched on for its company.
    private async Task<Result<(TerminalSession Session, Location Location)>> TerminalLocationAsync(ApplicationDbContext ctx, Guid terminalId)
    {
        Result<TerminalSession> session = await _terminalService.GetActiveSessionAsync(terminalId);

        if (!session.IsSuccess || session.Data is null)
        {
            return Result<(TerminalSession, Location)>.Fail(session.Error ?? "This device has been unpaired.");
        }

        Location? location = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(x => x.Id == session.Data.LocationId && x.IsActive);

        if (location is null)
        {
            return Result<(TerminalSession, Location)>.Fail("This device has been unpaired.");
        }

        if (!await ChecklistRunStore.FeaturesOnAsync(ctx, location.CompanyId))
        {
            return Result<(TerminalSession, Location)>.Fail(FeatureOff);
        }

        return Result<(TerminalSession, Location)>.Ok((session.Data, location));
    }

    // The location, if the person may use its checklists: it's active, they belong to it (Admins of its
    // company excepted), and the company has checklists and shift management switched on.
    private static async Task<Result<Location>> LocationForStaffAsync(ApplicationDbContext ctx, string userId, LocationScope scope, int locationId)
    {
        Location? location = await ctx.Locations.AsNoTracking().TagWithCallSite().Include(x => x.Company).FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

        if (location is null)
        {
            return Result<Location>.Fail("Location not found.");
        }

        bool member = (scope.IsAdmin && (scope.CompanyId is null || scope.CompanyId == location.CompanyId))
            || await ctx.UserLocationMemberships.AsNoTracking().TagWithCallSite().AnyAsync(x => x.UserId == userId && x.LocationId == locationId);

        if (!member)
        {
            return Result<Location>.Fail("You don't work at that location.");
        }

        if (!await ChecklistRunStore.FeaturesOnAsync(ctx, location.CompanyId))
        {
            return Result<Location>.Fail(FeatureOff);
        }

        return Result<Location>.Ok(location);
    }
}

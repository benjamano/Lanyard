using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Checklists;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.Projection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Checklists;

public class ChecklistRunService(
    IDbContextFactory<ApplicationDbContext> factory,
    IClockInTerminalService terminalService,
    ITimeEntryService timeEntryService,
    ITerminalEphemeralTokenService tokenService,
    Lanyard.Application.Services.VideoStreaming.IVideoStreamTokenService viewerTokens,
    IProjectionProgramRunnerService projectionRunner,
    IWidgetVisibilityService widgetVisibility,
    TimeProvider timeProvider,
    ILogger<ChecklistRunService> logger) : IChecklistRunService
{
    public const int MaxHistoryDays = 62;

    private const string FeatureOff = "Checklists aren't switched on for this location.";
    private const string GrantLapsed = "Enter your PIN again to keep ticking.";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IClockInTerminalService _terminalService = terminalService;
    private readonly ITimeEntryService _timeEntryService = timeEntryService;
    private readonly ITerminalEphemeralTokenService _tokenService = tokenService;
    private readonly Lanyard.Application.Services.VideoStreaming.IVideoStreamTokenService _viewerTokens = viewerTokens;
    private readonly IProjectionProgramRunnerService _projectionRunner = projectionRunner;
    private readonly IWidgetVisibilityService _widgetVisibility = widgetVisibility;
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

    public async Task<Result<bool>> CompleteAsync(string userId, LocationScope scope, Guid runItemId, string? note, decimal? reading,
        ChecklistCompletionMethod method = ChecklistCompletionMethod.Phone)
    {
        if (method is not (ChecklistCompletionMethod.Phone or ChecklistCompletionMethod.Manager))
        {
            return Result<bool>.Fail("Ticks from here are made on a phone or by a manager.");
        }

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

            if (method == ChecklistCompletionMethod.Manager && !IsManagerAt(scope, item.Run.LocationId))
            {
                return Result<bool>.Fail("Only a Manager or Admin here can tick from Checklists today.");
            }

            return await TickAsync(ctx, item, userId, IsManagerAt(scope, item.Run.LocationId), method, null, note, reading, now);
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
            .Include(x => x.Completions.Where(c => c.IsActive))
            .ThenInclude(x => x.Client)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == runItemId);

    // A tick from any path (phone, manager, terminal), once the caller has checked the person may use
    // the location: only today's tasks, one live tick per task, the eligibility rule, and the
    // answer the task asks for.
    // A kiosk tick (userId null, kioskId set) has nobody to check the rota or position rule against:
    // the caller has already checked the kiosk and that a Checklist card lets it tick.
    private async Task<Result<bool>> TickAsync(ApplicationDbContext ctx, ChecklistRunItem item, string? userId, bool isManagerAtLocation,
        ChecklistCompletionMethod method, Guid? terminalId, string? note, decimal? reading, DateTime now, Guid? kioskId = null)
    {
        ChecklistRun run = item.Run!;

        if (run.VenueDate != RotaTime.Today(now))
        {
            return Result<bool>.Fail("Only today's checklists can be ticked.");
        }

        if (item.Completions.FirstOrDefault() is ChecklistCompletion already)
        {
            return Result<bool>.Fail($"{ChecklistRunStore.WhoTicked(already)} ticked this at {RotaTime.ToVenueLocal(already.CompletedUtc).ToString("HH:mm", RotaFormat.Uk)}.");
        }

        if (userId is not null)
        {
            ChecklistRunStore.Viewer viewer = await ChecklistRunStore.ViewerAsync(ctx, userId, run.LocationId, run.VenueDate, isManagerAtLocation);
            Checklist? checklist = run.Checklist;

            if (ChecklistRules.WhyCantTick(viewer.IsManagerAtLocation, viewer.IsOnRota,
                    checklist?.StaffPositionId is Guid p && viewer.Positions.Contains(p),
                    checklist?.StaffPositionId, checklist?.StaffPosition?.Name, run.Name) is string reason)
            {
                return Result<bool>.Fail(reason);
            }
        }
        else if (kioskId is null)
        {
            return Result<bool>.Fail("Couldn't tell who ticked it.");
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
            ClientId = kioskId,
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
        catch (DbUpdateException ex) when (ChecklistTickConflicts.For(ex) is string conflict)
        {
            return Result<bool>.Fail(conflict);
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
            Result<TerminalSession> session = await _terminalService.GetActiveSessionAsync(terminalId);

            if (!session.IsSuccess || session.Data is null)
            {
                return Result<List<TerminalChecklistPerson>>.Fail(session.Error ?? "This device has been unpaired.");
            }

            int locationId = session.Data.LocationId;
            DateOnly today = RotaTime.Today(Now);
            DateTime start = RotaTime.StartOfDayUtc(today);
            DateTime end = RotaTime.StartOfDayUtc(today.AddDays(1));

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // The same people ChecklistRunStore.ViewerAsync counts as on the rota - a published shift
            // here starting today, or clocked in here now - plus the location's Managers. Unlike the
            // clock-in roster, nobody drops off once their clock-in window has passed.
            List<string> onRota = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.PublishedDateUtc != null && x.UserId != null && x.LocationId == locationId
                    && x.StartUtc >= start && x.StartUtc < end)
                .Select(x => x.UserId!)
                .ToListAsync();

            List<string> clockedIn = await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.LocationId == locationId && x.ClockOutUtc == null)
                .Select(x => x.UserId)
                .ToListAsync();

            List<string> managers = await SchedulingRecipients.ManagersOfLocationAsync(ctx, locationId);

            List<string> candidates = [.. onRota.Concat(clockedIn).Concat(managers)
                .Where(x => x != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Distinct()];

            // Only the location's own people: the PIN check (VerifyTerminalPinAsync) and clocking in
            // both need membership, so anyone else listed here could never get past their PIN.
            List<string> userIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && candidates.Contains(x.UserId))
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync();

            DateTimeOffset nowOffset = new(Now, TimeSpan.Zero);

            List<UserProfile> users = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => userIds.Contains(x.Id) && (x.LockoutEnd == null || x.LockoutEnd < nowOffset))
                .ToListAsync();

            HashSet<string> withPin = [.. await ctx.UserClockInPins
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => userIds.Contains(x.UserId))
                .Select(x => x.UserId)
                .ToListAsync()];

            return Result<List<TerminalChecklistPerson>>.Ok([.. users
                .Select(x => new TerminalChecklistPerson(x.Id, RotaNames.Public(x), withPin.Contains(x.Id)))
                .OrderBy(x => x.DisplayName)]);
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

        if (!await ChecklistRunStore.FeaturesOnAsync(ctx, location.Id))
        {
            return Result<(TerminalSession, Location)>.Fail(FeatureOff);
        }

        return Result<(TerminalSession, Location)>.Ok((session.Data, location));
    }

    public async Task<Result<ChecklistRunView?>> GetCardAsync(Guid checklistId, int companyId)
    {
        try
        {
            DateTime now = Now;
            DateOnly today = RotaTime.Today(now);

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Named company, not the tenant: kiosk dashboards run signed out (unfiltered).
            Checklist? checklist = await ctx.Checklists
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.Id == checklistId && x.CompanyId == companyId && x.IsActive);

            if (checklist?.Location is null || !checklist.Location.IsActive)
            {
                return Result<ChecklistRunView?>.Fail("That checklist isn't available any more.");
            }

            if (!await ChecklistRunStore.FeaturesOnAsync(ctx, companyId))
            {
                return Result<ChecklistRunView?>.Fail(FeatureOff);
            }

            await ChecklistRunStore.EnsureRunsAsync(ctx, checklist.Location, today, now);

            List<ChecklistRunView> runs = await ChecklistRunStore.LoadRunsAsync(ctx, checklist.LocationId, today, now, null, staffView: true);

            return Result<ChecklistRunView?>.Ok(runs.FirstOrDefault(x => x.ChecklistId == checklistId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the checklist card for {ChecklistId}", checklistId);
            return Result<ChecklistRunView?>.Fail($"Failed to load the checklist: {ex.Message}");
        }
    }

    public async Task<Result<bool>> CompleteOnKioskAsync(Guid clientId, string? viewerToken, Guid widgetId, Guid runItemId, string? note, decimal? reading)
    {
        try
        {
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Result<Client> kiosk = await KioskAsync(ctx, clientId, viewerToken);

            if (!kiosk.IsSuccess || kiosk.Data is null)
            {
                return Result<bool>.Fail(kiosk.Error!);
            }

            ChecklistRunItem? item = await LoadItemAsync(ctx, runItemId);

            // Only the kiosk's own company, and only through the card on its screen that allows it.
            if (item?.Run is null || item.Run.CompanyId != kiosk.Data.CompanyId)
            {
                return Result<bool>.Fail("That task wasn't found.");
            }

            if (await WhyKioskCantTickAsync(ctx, kiosk.Data, widgetId, item.Run.ChecklistId, now) is string refused)
            {
                return Result<bool>.Fail(refused);
            }

            return await TickAsync(ctx, item, null, false, ChecklistCompletionMethod.Kiosk, null, note, reading, now, clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to tick checklist task {RunItemId} on kiosk {ClientId}", runItemId, clientId);
            return Result<bool>.Fail($"Failed to tick the task: {ex.Message}");
        }
    }

    public async Task<Result<bool>> CanTickOnKioskAsync(Guid clientId, string? viewerToken, Guid widgetId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Result<Client> kiosk = await KioskAsync(ctx, clientId, viewerToken);

            if (!kiosk.IsSuccess || kiosk.Data is null)
            {
                return Result<bool>.Ok(false);
            }

            Guid? checklistId = await ctx.DashboardWidgets
                .OfType<ChecklistCardWidget>()
                .AsNoTracking()
                .TagWithCallSite()
                .Where(w => w.Id == widgetId)
                .Select(w => w.ChecklistCardChecklistId)
                .FirstOrDefaultAsync();

            bool sameCompany = checklistId is Guid id && await ctx.Checklists
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(x => x.Id == id && x.CompanyId == kiosk.Data.CompanyId && x.IsActive);

            return Result<bool>.Ok(sameCompany && await WhyKioskCantTickAsync(ctx, kiosk.Data, widgetId, checklistId!.Value, Now) is null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check kiosk {ClientId} may tick through card {WidgetId}", clientId, widgetId);
            return Result<bool>.Fail($"Failed to check the kiosk: {ex.Message}");
        }
    }

    public async Task<Result<bool>> UndoOnKioskAsync(Guid clientId, string? viewerToken, Guid widgetId, Guid completionId)
    {
        try
        {
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Result<Client> kiosk = await KioskAsync(ctx, clientId, viewerToken);

            if (!kiosk.IsSuccess || kiosk.Data is null)
            {
                return Result<bool>.Fail(kiosk.Error!);
            }

            ChecklistCompletion? completion = await ctx.ChecklistCompletions
                .TagWithCallSite()
                .Include(x => x.RunItem)
                .ThenInclude(x => x!.Run)
                .FirstOrDefaultAsync(x => x.Id == completionId && x.IsActive);

            ChecklistRun? run = completion?.RunItem?.Run;

            if (completion is null || run is null || run.CompanyId != kiosk.Data.CompanyId)
            {
                return Result<bool>.Fail("That tick wasn't found.");
            }

            // A person's tick isn't the kiosk's to take back; they (or a manager) can, on their phone.
            if (completion.Method != ChecklistCompletionMethod.Kiosk)
            {
                return Result<bool>.Fail("Only ticks made on a kiosk can be taken back here.");
            }

            if (run.VenueDate != RotaTime.Today(now))
            {
                return Result<bool>.Fail("Only today's ticks can be taken back.");
            }

            if (await WhyKioskCantTickAsync(ctx, kiosk.Data, widgetId, run.ChecklistId, now) is string refused)
            {
                return Result<bool>.Fail(refused);
            }

            completion.IsActive = false;
            completion.UndoneUtc = now;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Checklist tick {CompletionId} taken back on kiosk {ClientId}", completionId, clientId);

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to take back checklist tick {CompletionId} on kiosk {ClientId}", completionId, clientId);
            return Result<bool>.Fail($"Failed to take the tick back: {ex.Message}");
        }
    }

    // The kiosk, if this page really is its display: the viewer token is only ever handed to a kiosk
    // over its authenticated hub connection, so a page without it (or with another kiosk's) is
    // refused. Its company must have checklists switched on.
    private async Task<Result<Client>> KioskAsync(ApplicationDbContext ctx, Guid clientId, string? viewerToken)
    {
        if (!_viewerTokens.ValidateViewerToken(clientId, viewerToken))
        {
            return Result<Client>.Fail("This screen can't tick checklists.");
        }

        Client? client = await ctx.Clients.AsNoTracking().TagWithCallSite().FirstOrDefaultAsync(x => x.Id == clientId);

        if (client is null)
        {
            return Result<Client>.Fail("This screen can't tick checklists.");
        }

        if (!await ChecklistRunStore.FeaturesOnAsync(ctx, client.CompanyId))
        {
            return Result<Client>.Fail(FeatureOff);
        }

        return Result<Client>.Ok(client);
    }

    // Why this kiosk can't tick this checklist through this card right now, or null when it can. The
    // card must allow kiosk ticking for this checklist, sit on an active dashboard of the kiosk's
    // company, be on the kiosk's screen (the step it's showing now is that dashboard) and be shown
    // (its "Show only when…" conditions hold). So a read-only or hidden card elsewhere never opens
    // ticking up, and neither does another kiosk in the same company.
    private async Task<string?> WhyKioskCantTickAsync(ApplicationDbContext ctx, Client kiosk, Guid widgetId, Guid checklistId, DateTime now)
    {
        const string NotHere = "This checklist can't be ticked on this screen.";

        ChecklistCardWidget? card = await ctx.DashboardWidgets
            .OfType<ChecklistCardWidget>()
            .AsNoTracking()
            .TagWithCallSite()
            .FirstOrDefaultAsync(w => w.Id == widgetId && w.IsActive && w.ChecklistCardAllowKioskTicking && w.ChecklistCardChecklistId == checklistId
                && ctx.Dashboards.Any(d => d.Id == w.DashboardId && d.IsActive && d.CompanyId == kiosk.CompanyId));

        if (card is null)
        {
            return NotHere;
        }

        if (!IsShowingDashboard(kiosk.Id, card.DashboardId))
        {
            return "This screen isn't showing that checklist right now.";
        }

        Result<WidgetVisibilityLookups> lookups = await _widgetVisibility.LoadAsync([card]);

        if (!lookups.IsSuccess || lookups.Data is null || !_widgetVisibility.IsShown(card, kiosk.CompanyId, lookups.Data, now))
        {
            return "This checklist isn't shown on this screen right now.";
        }

        return null;
    }

    // Whether any of the kiosk's displays is on a Dashboard step for this dashboard, by the projection
    // runner (the server's own record of what each kiosk is playing).
    private bool IsShowingDashboard(Guid clientId, Guid dashboardId) =>
        _projectionRunner.GetRunningStates().Any(run => run.ClientId == clientId
            && run.CurrentStepIndex >= 0 && run.CurrentStepIndex < run.Steps.Count
            && run.Steps[run.CurrentStepIndex] is ProjectionProgramStep step
            && step.Template?.Name == ProjectionTemplateNames.Dashboard
            && step.ParameterValues.Any(v => v.Parameter?.Name == ProjectionParameterNames.Dashboard
                && Guid.TryParse(v.Value, out Guid shown) && shown == dashboardId));

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

        if (!await ChecklistRunStore.FeaturesOnAsync(ctx, location.Id))
        {
            return Result<Location>.Fail(FeatureOff);
        }

        return Result<Location>.Ok(location);
    }
}

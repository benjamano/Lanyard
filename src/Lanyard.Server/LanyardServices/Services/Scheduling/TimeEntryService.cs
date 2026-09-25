using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class TimeEntryService(
    IDbContextFactory<ApplicationDbContext> factory,
    IClockInPinService pinService,
    IClockInTerminalService terminalService,
    ISchedulingSettingsService settingsService,
    ITerminalEphemeralTokenService tokenService,
    ITerminalEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<TimeEntryService> logger) : ITimeEntryService
{
    // Nobody works 16 hours straight here; an entry still open after that is someone who forgot to
    // clock out, and is closed (and flagged) so they can clock in for their next shift.
    public static readonly TimeSpan StaleOpenEntry = TimeSpan.FromHours(16);

    private static readonly TimeSpan MaxEntryLength = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IClockInPinService _pinService = pinService;
    private readonly IClockInTerminalService _terminalService = terminalService;
    private readonly ISchedulingSettingsService _settingsService = settingsService;
    private readonly ITerminalEphemeralTokenService _tokenService = tokenService;
    private readonly ITerminalEventBus _eventBus = eventBus;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<TimeEntryService> _logger = logger;

    private DateTime Now => _timeProvider.GetUtcNow().UtcDateTime;

    private string TooManyWrongPins(DateTime lockedUntilUtc)
    {
        TimeSpan wait = lockedUntilUtc - Now;
        string when = wait <= TimeSpan.FromSeconds(60)
            ? $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} seconds"
            : $"{(int)Math.Ceiling(wait.TotalMinutes)} minutes";

        return $"Too many wrong PINs. Try again in {when}, or scan the QR code with your phone.";
    }

    public async Task<Result<List<TerminalRosterEntry>>> GetTerminalRosterAsync(Guid terminalId)
    {
        try
        {
            Result<TerminalSession> session = await _terminalService.GetActiveSessionAsync(terminalId);

            if (!session.IsSuccess || session.Data is null)
            {
                return Result<List<TerminalRosterEntry>>.Fail(session.Error ?? "This device has been unpaired.");
            }

            int locationId = session.Data.LocationId;
            DateTime now = Now;
            DateOnly today = RotaTime.Today(now);
            DateTime dayEndUtc = RotaTime.StartOfDayUtc(today.AddDays(1));

            Result<CompanySchedulingSettings> settings = await _settingsService.GetSettingsAsync(session.Data.CompanyId);
            DateTime stillClockableFrom = now.AddMinutes(-(settings.Data?.ClockInWindowMinutes ?? 60));

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Today's shifts that can still be clocked in for: not yet started, under way, or
            // finished within the clock-in window. A shift that ended this morning isn't listed -
            // that person can't clock in for it any more, and anyone still on the clock is picked
            // up below regardless.
            List<Shift> todaysShifts = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Where(x => x.LocationId == locationId && x.IsActive && x.PublishedDateUtc != null
                    && x.StartUtc < dayEndUtc && x.EndUtc > stillClockableFrom
                    && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            // An entry left open longer than StaleOpenEntry is someone who forgot to clock out; their
            // next tap closes it and clocks them *in* (ClockAsync), so they aren't shown as on the
            // clock here either.
            DateTime freshFrom = now - StaleOpenEntry;

            List<TimeEntry> openHere = await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Where(x => x.LocationId == locationId && x.IsActive && x.ClockOutUtc == null && x.ClockInUtc > freshFrom
                    && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .ToListAsync();

            List<string> userIds = todaysShifts.Select(x => x.UserId).Concat(openHere.Select(x => x.UserId)).Distinct().ToList();

            HashSet<string> clockedIn = (await ctx.TimeEntries
                .AsNoTracking()
                .Where(x => userIds.Contains(x.UserId) && x.IsActive && x.ClockOutUtc == null && x.ClockInUtc > freshFrom)
                .Select(x => x.UserId)
                .ToListAsync()).ToHashSet();

            HashSet<string> withPin = (await ctx.UserClockInPins
                .AsNoTracking()
                .Where(x => userIds.Contains(x.UserId))
                .Select(x => x.UserId)
                .ToListAsync()).ToHashSet();

            Dictionary<string, UserProfile> users = todaysShifts.Select(x => x.User!)
                .Concat(openHere.Select(x => x.User!))
                .Where(x => x is not null)
                .DistinctBy(x => x.Id)
                .ToDictionary(x => x.Id);

            List<TerminalRosterEntry> roster = userIds
                .Where(users.ContainsKey)
                .Select(id =>
                {
                    Shift? shift = todaysShifts.FirstOrDefault(x => x.UserId == id);

                    return new TerminalRosterEntry(
                        id,
                        RotaNames.Public(users[id]),
                        shift is null ? null : RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc),
                        shift?.StartUtc,
                        clockedIn.Contains(id),
                        withPin.Contains(id));
                })
                .OrderBy(x => x.ShiftStartUtc ?? DateTime.MaxValue)
                .ThenBy(x => x.DisplayName)
                .ToList();

            return Result<List<TerminalRosterEntry>>.Ok(roster);
        }
        catch (Exception ex)
        {
            return Result<List<TerminalRosterEntry>>.Fail($"Failed to load today's staff: {ex.Message}");
        }
    }

    public async Task<Result<ClockActionResult>> ClockByPinAsync(Guid terminalId, string userId, string pin)
    {
        try
        {
            Result<TerminalSession> session = await _terminalService.GetActiveSessionAsync(terminalId);

            if (!session.IsSuccess || session.Data is null)
            {
                return Result<ClockActionResult>.Fail(session.Error ?? "This device has been unpaired.");
            }

            if (_tokenService.PinLockedUntil(userId) is DateTime lockedUntil)
            {
                return Result<ClockActionResult>.Fail(TooManyWrongPins(lockedUntil));
            }

            Result<ClockInPinStatus> status = await _pinService.GetStatusAsync(userId);

            if (status.IsSuccess && status.Data is { HasPin: false })
            {
                return Result<ClockActionResult>.Fail("You haven't set a clock-in PIN yet. Scan the QR code with your phone instead, or ask a manager to set one.");
            }

            Result<bool> verified = await _pinService.VerifyPinAsync(userId, pin);

            if (!verified.IsSuccess)
            {
                return Result<ClockActionResult>.Fail(verified.Error ?? "Couldn't check your PIN.");
            }

            if (!verified.Data)
            {
                DateTime? nowLockedUntil = _tokenService.RegisterPinFailure(userId);

                if (nowLockedUntil is not null)
                {
                    _logger.LogWarning("PIN locked for {UserId} after repeated wrong PINs at terminal {TerminalId}", userId, terminalId);
                }

                return Result<ClockActionResult>.Fail(nowLockedUntil is DateTime until
                    ? TooManyWrongPins(until)
                    : "That PIN isn't right. Try again.");
            }

            _tokenService.ClearPinFailures(userId);

            return await ClockAsync(session.Data, userId, ClockMethod.Pin);
        }
        catch (Exception ex)
        {
            return Result<ClockActionResult>.Fail($"Couldn't clock you in or out: {ex.Message}");
        }
    }

    public async Task<Result<ClockActionResult>> ClockByQrAsync(string nonce, string userId)
    {
        try
        {
            // Single-use: the code only ever existed on the terminal's screen for a minute, which is
            // what makes scanning it evidence the person is standing at the terminal.
            Guid? terminalId = _tokenService.ConsumeQrNonce(nonce);

            if (terminalId is null)
            {
                return Result<ClockActionResult>.Fail("That code has expired. Scan the code on the tablet again.");
            }

            Result<TerminalSession> session = await _terminalService.GetActiveSessionAsync(terminalId.Value);

            if (!session.IsSuccess || session.Data is null)
            {
                return Result<ClockActionResult>.Fail(session.Error ?? "That tablet has been unpaired.");
            }

            return await ClockAsync(session.Data, userId, ClockMethod.Qr);
        }
        catch (Exception ex)
        {
            return Result<ClockActionResult>.Fail($"Couldn't clock you in or out: {ex.Message}");
        }
    }

    private async Task<Result<ClockActionResult>> ClockAsync(TerminalSession session, string userId, ClockMethod method)
    {
        DateTime now = Now;

        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

        UserProfile? user = await ctx.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            return Result<ClockActionResult>.Fail("We couldn't find your account.");
        }

        TimeEntry? open = await ctx.TimeEntries
            .Include(x => x.Location)
            .Include(x => x.Shift)
            .FirstOrDefaultAsync(x => x.UserId == userId && x.IsActive && x.ClockOutUtc == null);

        if (open is not null && now - open.ClockInUtc > StaleOpenEntry)
        {
            open.ClockOutUtc = open.ClockInUtc.Add(StaleOpenEntry);
            open.ClockOutMethod = ClockMethod.Automatic;
            open.NeedsReview = true;
            open.ReviewReason = "Closed automatically after 16 hours because nobody clocked out.";
            open.UpdateDate = now;

            await ctx.SaveChangesAsync();

            _logger.LogWarning("Closed stale time entry {EntryId} for {UserId} before a new clock action", open.Id, userId);

            open = null;
        }

        if (open is not null)
        {
            // Clocking out is always allowed - finishing late, early or somewhere unexpected is for
            // a manager to review, not a reason to leave someone stuck on the clock.
            if (open.LocationId != session.LocationId)
            {
                open.NeedsReview = true;
                open.ReviewReason = $"Clocked in at {open.Location?.Name ?? "another location"} but out at {session.LocationName}.";
            }

            open.ClockOutUtc = now;
            open.ClockOutMethod = method;
            open.UpdateDate = now;

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Clocked out {UserId} at location {LocationId} via {Method}", userId, session.LocationId, method);

            return Result<ClockActionResult>.Ok(Publish(session, user, method, ClockDirection.Out, now,
                open.Shift is null ? null : RotaFormat.TimeRange(open.Shift.StartUtc, open.Shift.EndUtc), open.ClockedHours));
        }

        bool isMember = await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == userId && x.LocationId == session.LocationId);

        if (!isMember)
        {
            return Result<ClockActionResult>.Fail($"You're not set up to work at {session.LocationName}. Ask a manager.");
        }

        Result<CompanySchedulingSettings> settings = await _settingsService.GetSettingsAsync(session.CompanyId);
        TimeSpan window = TimeSpan.FromMinutes(settings.Data?.ClockInWindowMinutes ?? 60);

        DateTime earliestStart = now.Add(-MaxEntryLength).Add(-window);
        List<Shift> candidates = await ctx.Shifts
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.LocationId == session.LocationId && x.IsActive && x.PublishedDateUtc != null
                && x.StartUtc > earliestStart && x.StartUtc <= now.Add(window))
            .ToListAsync();

        Shift? shift = candidates
            .Where(x => x.StartUtc.Add(-window) <= now && x.EndUtc.Add(window) >= now)
            .OrderBy(x => Math.Abs((x.StartUtc - now).Ticks))
            .FirstOrDefault();

        if (shift is null)
        {
            Shift? next = await ctx.Shifts
                .AsNoTracking()
                .Where(x => x.UserId == userId && x.LocationId == session.LocationId && x.IsActive && x.PublishedDateUtc != null && x.StartUtc > now)
                .OrderBy(x => x.StartUtc)
                .FirstOrDefaultAsync();

            string nextText = next is null ? string.Empty : $" Your next shift here is {RotaFormat.DayAndTime(next.StartUtc)}.";

            return Result<ClockActionResult>.Fail($"You're not on the rota right now.{nextText} If you're covering a shift, ask a manager to add your time.");
        }

        TimeEntry entry = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LocationId = session.LocationId,
            ShiftId = shift.Id,
            ClockInTerminalId = session.TerminalId,
            ClockInUtc = now,
            ClockInMethod = method,
            CreateDate = now,
            IsActive = true
        };

        ctx.TimeEntries.Add(entry);

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The one-open-entry index caught a simultaneous clock-in from another device.
            return Result<ClockActionResult>.Fail("You're already clocked in.");
        }

        _logger.LogInformation("Clocked in {UserId} at location {LocationId} via {Method} against shift {ShiftId}", userId, session.LocationId, method, shift.Id);

        return Result<ClockActionResult>.Ok(Publish(session, user, method, ClockDirection.In, now, RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc), null));
    }

    private ClockActionResult Publish(TerminalSession session, UserProfile user, ClockMethod method, ClockDirection direction, DateTime atUtc, string? shiftSummary, decimal? clockedHours)
    {
        ClockActionResult result = new(user.GetGreetingName(), direction, atUtc, shiftSummary, clockedHours);

        _eventBus.Publish(new TerminalClockEvent(session.TerminalId, result.GreetingName, direction, atUtc, method));

        return result;
    }

    public async Task<Result<ClockState>> GetClockStateAsync(string userId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeEntry? open = await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .FirstOrDefaultAsync(x => x.UserId == userId && x.IsActive && x.ClockOutUtc == null);

            return Result<ClockState>.Ok(open is null
                ? new ClockState(false, null, null)
                : new ClockState(true, open.ClockInUtc, open.Location?.Name));
        }
        catch (Exception ex)
        {
            return Result<ClockState>.Fail($"Failed to check whether you're clocked in: {ex.Message}");
        }
    }

    public async Task<Result<TimesheetView>> GetTimesheetAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to)
    {
        try
        {
            if (to < from)
            {
                return Result<TimesheetView>.Fail("The end of the range must not be before the start.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<TimesheetView>.Fail("You can only view timesheets for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == locationId);

            if (location is null)
            {
                return Result<TimesheetView>.Fail("Location not found.");
            }

            DateTime startUtc = RotaTime.StartOfDayUtc(from);
            DateTime endUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            List<TimeEntry> entries = await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Include(x => x.Shift)
                .Include(x => x.ClockInTerminal)
                .Where(x => x.LocationId == locationId && x.IsActive && x.ClockInUtc >= startUtc && x.ClockInUtc < endUtc)
                .ToListAsync();

            List<Shift> shifts = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Where(x => x.LocationId == locationId && x.IsActive && x.PublishedDateUtc != null && x.StartUtc >= startUtc && x.StartUtc < endUtc)
                .ToListAsync();

            HashSet<Guid> workedShiftIds = entries.Where(x => x.ShiftId != null).Select(x => x.ShiftId!.Value).ToHashSet();

            List<(string UserId, UserProfile? User, TimesheetLine Line)> lines = entries
                .Select(e => (e.UserId, e.User, new TimesheetLine(RotaTime.LocalDate(e.ClockInUtc), e.Shift, e)))
                .Concat(shifts
                    .Where(s => !workedShiftIds.Contains(s.Id))
                    .Select(s => (s.UserId, s.User, new TimesheetLine(RotaTime.LocalDate(s.StartUtc), s, (TimeEntry?)null))))
                .ToList();

            List<TimesheetPerson> people = lines
                .GroupBy(x => x.UserId)
                .Select(g =>
                {
                    List<TimesheetLine> personLines = g
                        .Select(x => x.Line)
                        .OrderBy(x => x.Entry?.ClockInUtc ?? x.Shift!.StartUtc)
                        .ToList();

                    List<TimeEntry> personEntries = personLines.Where(x => x.Entry is not null).Select(x => x.Entry!).ToList();

                    return new TimesheetPerson(
                        g.Key,
                        RotaNames.For(g.Select(x => x.User).FirstOrDefault(u => u is not null)),
                        personLines,
                        personEntries.Sum(x => x.ClockedHours ?? 0m),
                        personLines.Where(x => x.Shift is not null).Select(x => x.Shift!).DistinctBy(x => x.Id).Sum(x => x.PaidHours),
                        personEntries.Count(x => x.NeedsReview),
                        personEntries.Count(x => !x.IsOpen && !x.IsApproved));
                })
                .OrderBy(x => x.DisplayName)
                .ToList();

            return Result<TimesheetView>.Ok(new TimesheetView(locationId, location.Name, from, to, people));
        }
        catch (Exception ex)
        {
            return Result<TimesheetView>.Fail($"Failed to load the timesheet: {ex.Message}");
        }
    }

    public async Task<Result<List<TimeEntry>>> GetEntriesForUserAsync(string userId, DateOnly from, DateOnly to)
    {
        try
        {
            DateTime startUtc = RotaTime.StartOfDayUtc(from);
            DateTime endUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<TimeEntry> entries = await ctx.TimeEntries
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.Shift)
                .Where(x => x.UserId == userId && x.IsActive && x.ClockInUtc >= startUtc && x.ClockInUtc < endUtc)
                .OrderByDescending(x => x.ClockInUtc)
                .ToListAsync();

            return Result<List<TimeEntry>>.Ok(entries);
        }
        catch (Exception ex)
        {
            return Result<List<TimeEntry>>.Fail($"Failed to load your hours: {ex.Message}");
        }
    }

    public async Task<Result<TimeEntry>> SaveEntryAsync(LocationScope scope, TimeEntry entry, string actingUserId)
    {
        try
        {
            DateTime now = Now;

            if (entry.ClockOutUtc is DateTime outUtc)
            {
                if (outUtc <= entry.ClockInUtc)
                {
                    return Result<TimeEntry>.Fail("The clock-out time must be after the clock-in time.");
                }

                if (outUtc - entry.ClockInUtc > MaxEntryLength)
                {
                    return Result<TimeEntry>.Fail("A single entry can't be longer than 24 hours.");
                }
            }

            // A clock-out in the future too: the terminal only checks for an open entry, so a
            // closed 09:00-17:00 entry typed in at 10:00 would let a clock-in at 12:00 overlap it.
            if (entry.ClockInUtc > now.AddMinutes(5) || entry.ClockOutUtc > now.AddMinutes(5))
            {
                return Result<TimeEntry>.Fail("Time can't be recorded in the future.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, entry.LocationId))
            {
                return Result<TimeEntry>.Fail("You can only edit timesheets for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeEntry? existing = null;

            if (entry.Id != Guid.Empty)
            {
                existing = await ctx.TimeEntries.FirstOrDefaultAsync(x => x.Id == entry.Id);

                if (existing is null || !existing.IsActive)
                {
                    return Result<TimeEntry>.Fail("That time entry no longer exists.");
                }

                if (existing.LocationId != entry.LocationId)
                {
                    return Result<TimeEntry>.Fail("A time entry can't be moved to a different location.");
                }
            }

            if (existing is null || existing.UserId != entry.UserId)
            {
                bool isMember = await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == entry.UserId && x.LocationId == entry.LocationId);

                if (!isMember)
                {
                    return Result<TimeEntry>.Fail("That person isn't a member of this location.");
                }
            }

            DateTime newOut = entry.ClockOutUtc ?? DateTime.MaxValue;
            Guid selfId = existing?.Id ?? Guid.Empty;

            TimeEntry? clash = await ctx.TimeEntries
                .AsNoTracking()
                .Where(x => x.UserId == entry.UserId && x.IsActive && x.Id != selfId
                    && x.ClockInUtc < newOut && (x.ClockOutUtc == null || x.ClockOutUtc > entry.ClockInUtc))
                .OrderBy(x => x.ClockInUtc)
                .FirstOrDefaultAsync();

            if (clash is not null)
            {
                string clashEnd = clash.ClockOutUtc is DateTime clashOut ? RotaFormat.LocalTime(clashOut) : "now (still clocked in)";

                return Result<TimeEntry>.Fail($"This overlaps time already recorded from {RotaFormat.DayAndTime(clash.ClockInUtc)} to {clashEnd}.");
            }

            // Link to the published shift the time was worked against, if any overlaps it.
            DateTime linkEnd = entry.ClockOutUtc ?? entry.ClockInUtc.AddHours(1);

            Guid? shiftId = (await ctx.Shifts
                .AsNoTracking()
                .Where(x => x.UserId == entry.UserId && x.LocationId == entry.LocationId && x.IsActive && x.PublishedDateUtc != null
                    && x.StartUtc < linkEnd && x.EndUtc > entry.ClockInUtc)
                .Select(x => new { x.Id, x.StartUtc })
                .ToListAsync())
                .OrderBy(x => Math.Abs((x.StartUtc - entry.ClockInUtc).Ticks))
                .Select(x => (Guid?)x.Id)
                .FirstOrDefault();

            TimeEntry saved;

            if (existing is null)
            {
                saved = new TimeEntry
                {
                    Id = Guid.NewGuid(),
                    UserId = entry.UserId,
                    LocationId = entry.LocationId,
                    ClockInMethod = ClockMethod.Manual,
                    CreateDate = now,
                    CreateByUserId = actingUserId,
                    IsActive = true
                };

                ctx.TimeEntries.Add(saved);
            }
            else
            {
                saved = existing;
                saved.UserId = entry.UserId;
            }

            if (saved.ClockOutUtc != entry.ClockOutUtc && entry.ClockOutUtc is not null)
            {
                saved.ClockOutMethod = ClockMethod.Manual;
            }
            else if (entry.ClockOutUtc is null)
            {
                saved.ClockOutMethod = null;
            }

            saved.ClockInUtc = DateTime.SpecifyKind(entry.ClockInUtc, DateTimeKind.Utc);
            saved.ClockOutUtc = entry.ClockOutUtc is DateTime o ? DateTime.SpecifyKind(o, DateTimeKind.Utc) : null;
            saved.ShiftId = shiftId;
            saved.Notes = string.IsNullOrWhiteSpace(entry.Notes) ? null : entry.Notes.Trim();
            saved.UpdateDate = now;
            saved.UpdateByUserId = actingUserId;

            // A manager entering or correcting time has, by doing so, reviewed it.
            saved.NeedsReview = false;
            saved.ReviewReason = null;
            saved.ApprovedByUserId = saved.IsOpen ? null : actingUserId;
            saved.ApprovedDateUtc = saved.IsOpen ? null : now;

            try
            {
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Result<TimeEntry>.Fail("That person is already clocked in, so another open entry can't be added.");
            }

            return Result<TimeEntry>.Ok(saved);
        }
        catch (Exception ex)
        {
            return Result<TimeEntry>.Fail($"Failed to save the time entry: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ApproveEntryAsync(LocationScope scope, Guid entryId, string approverUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeEntry? entry = await ctx.TimeEntries.FirstOrDefaultAsync(x => x.Id == entryId && x.IsActive);

            if (entry is null)
            {
                return Result<bool>.Fail("That time entry no longer exists.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, entry.LocationId))
            {
                return Result<bool>.Fail("You can only approve timesheets for your own location.");
            }

            if (entry.IsOpen)
            {
                return Result<bool>.Fail("This person is still clocked in - approve it once they've clocked out.");
            }

            entry.ApprovedByUserId = approverUserId;
            entry.ApprovedDateUtc = Now;
            entry.NeedsReview = false;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to approve the time entry: {ex.Message}");
        }
    }

    public async Task<Result<int>> ApproveAllAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string approverUserId)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<int>.Fail("You can only approve timesheets for your own location.");
            }

            DateTime startUtc = RotaTime.StartOfDayUtc(from);
            DateTime endUtc = RotaTime.StartOfDayUtc(to.AddDays(1));
            DateTime now = Now;

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<TimeEntry> ready = await ctx.TimeEntries
                .Where(x => x.LocationId == locationId && x.IsActive && x.ClockOutUtc != null && x.ApprovedDateUtc == null && !x.NeedsReview
                    && x.ClockInUtc >= startUtc && x.ClockInUtc < endUtc)
                .ToListAsync();

            foreach (TimeEntry entry in ready)
            {
                entry.ApprovedByUserId = approverUserId;
                entry.ApprovedDateUtc = now;
            }

            await ctx.SaveChangesAsync();

            return Result<int>.Ok(ready.Count);
        }
        catch (Exception ex)
        {
            return Result<int>.Fail($"Failed to approve the timesheet: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeactivateEntryAsync(LocationScope scope, Guid entryId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            TimeEntry? entry = await ctx.TimeEntries.FirstOrDefaultAsync(x => x.Id == entryId && x.IsActive);

            if (entry is null)
            {
                return Result<bool>.Fail("That time entry no longer exists.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, entry.LocationId))
            {
                return Result<bool>.Fail("You can only edit timesheets for your own location.");
            }

            entry.IsActive = false;
            entry.UpdateDate = Now;
            entry.UpdateByUserId = actingUserId;

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove the time entry: {ex.Message}");
        }
    }
}

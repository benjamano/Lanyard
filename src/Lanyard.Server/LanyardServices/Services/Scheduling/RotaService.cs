using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Notifications;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Notifications;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class RotaService(
    IDbContextFactory<ApplicationDbContext> factory,
    IContractRequirementService contractRequirementService,
    INotificationDispatcher notifications,
    ILogger<RotaService> logger) : IRotaService
{
    private static readonly TimeSpan MaxShiftLength = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IContractRequirementService _contractRequirementService = contractRequirementService;
    private readonly INotificationDispatcher _notifications = notifications;
    private readonly ILogger<RotaService> _logger = logger;

    public async Task<Result<RotaRangeView>> GetRangeViewAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeAllMembers = false)
    {
        try
        {
            if (to < from)
            {
                return Result<RotaRangeView>.Fail("The end of the range must not be before the start.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<RotaRangeView>.Fail("You can only view the rota for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == locationId && x.IsActive);

            if (location is null)
            {
                return Result<RotaRangeView>.Fail("Location not found.");
            }

            int companyId = location.CompanyId;

            // Whole Monday-weeks around the range, so a month that starts on a Thursday still
            // judges that first week on all seven of its days.
            DateOnly firstWeek = RotaTime.GetWeekStart(from);
            DateOnly afterLastWeek = RotaTime.GetWeekStart(to).AddDays(7);
            DateTime windowStartUtc = RotaTime.StartOfDayUtc(firstWeek);
            DateTime windowEndUtc = RotaTime.StartOfDayUtc(afterLastWeek);

            List<Shift> locationShifts = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => x.LocationId == locationId
                    && (x.IsActive || x.RemovalPending)
                    && x.StartUtc >= windowStartUtc && x.StartUtc < windowEndUtc)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            List<string> memberIds = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Select(x => x.UserId)
                .ToListAsync();

            List<UserPosition> memberPositions = await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => memberIds.Contains(x.UserId) && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
                .ToListAsync();

            HashSet<string> rowUserIds = includeAllMembers
                ? memberIds.ToHashSet()
                : memberPositions.Select(x => x.UserId).ToHashSet();

            foreach (Shift shift in locationShifts.Where(x => x.UserId is not null))
            {
                rowUserIds.Add(shift.UserId!);
            }

            List<string> rowIds = rowUserIds.ToList();

            List<UserProfile> users = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => rowIds.Contains(x.Id))
                .ToListAsync();

            // Hours are counted company-wide: someone doing 20 h at Ipswich and 20 h at Wisbech has
            // a 40 h week, whichever site's rota is being looked at.
            List<int> companyLocationIds = await ctx.Locations
                .AsNoTracking()
                .Where(x => x.CompanyId == companyId)
                .Select(x => x.Id)
                .ToListAsync();

            List<Shift> companyShifts = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive
                    && x.UserId != null && rowIds.Contains(x.UserId)
                    && companyLocationIds.Contains(x.LocationId)
                    && x.StartUtc >= windowStartUtc && x.StartUtc < windowEndUtc)
                .ToListAsync();

            Result<Dictionary<string, ResolvedContract>> contractsResult =
                await _contractRequirementService.ResolveForUsersAsync(rowIds, companyId);

            Dictionary<string, ResolvedContract> contracts = contractsResult.IsSuccess && contractsResult.Data is not null
                ? contractsResult.Data
                : [];

            if (!contractsResult.IsSuccess)
            {
                _logger.LogWarning("Failed to resolve contracts for location {LocationId}: {Error}", locationId, contractsResult.Error);
            }

            // Time off beside the shifts: approved and still-pending, so a manager building the
            // rota sees "Tom has asked for Saturday off" before scheduling him.
            ILookup<string, TimeOffRequest> timeOffByUser =
                (await TimeOffService.LiveTimeOffQuery(ctx, rowIds, firstWeek, afterLastWeek.AddDays(-1)).ToListAsync())
                .ToLookup(x => x.UserId);

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            // Grouped once up front - each shift's venue-local week is worked out a single time,
            // instead of once per (person, week) pair inside the loops below.
            ILookup<(string UserId, DateOnly Week), Shift> companyShiftsByUserWeek =
                companyShifts.ToLookup(x => (x.UserId!, RotaTime.GetWeekStart(RotaTime.LocalDate(x.StartUtc))));
            ILookup<string, Shift> locationShiftsByUser = locationShifts.Where(x => x.UserId is not null).ToLookup(x => x.UserId!);
            Dictionary<string, UserPosition> primaryByUser = memberPositions
                .Where(x => x.IsPrimary)
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.First());

            HashSet<string> memberSet = memberIds.ToHashSet();
            List<DateOnly> weekStarts = [];

            for (DateOnly week = firstWeek; week < afterLastWeek; week = week.AddDays(7))
            {
                weekStarts.Add(week);
            }

            List<RotaStaffRow> rows = [];

            foreach (UserProfile user in users)
            {
                ResolvedContract contract = contracts.TryGetValue(user.Id, out ResolvedContract? resolved) ? resolved : ResolvedContract.Empty;
                bool isMember = memberSet.Contains(user.Id);

                Dictionary<DateOnly, decimal> hoursByWeek = [];
                List<ContractWarning> warnings = [];
                List<TimeOffRequest> userTimeOff = timeOffByUser[user.Id].ToList();
                List<TimeOffRequest> approvedTimeOff = userTimeOff.Where(x => x.Status == TimeOffStatus.Approved).ToList();

                foreach (DateOnly week in weekStarts)
                {
                    List<Shift> inWeek = companyShiftsByUserWeek[(user.Id, week)].ToList();

                    hoursByWeek[week] = inWeek.Sum(x => x.PaidHours);

                    // Only people who still work here are judged against their contract - a leaver
                    // with an old shift on the rota isn't "missing" hours.
                    if (isMember)
                    {
                        List<ContractWarning> weekWarnings = ContractCompliance.EvaluateWeek(user.Id, week, contract, inWeek);

                        // Someone booked off for the whole week can't be short of shifts that week.
                        // Only a whole week excuses it: a day or two off still leaves room for the
                        // "one shift a week" kind of contract to be met.
                        if (ContractCompliance.IsWholeWeekOff(week, approvedTimeOff))
                        {
                            weekWarnings.RemoveAll(w => w.Kind is ContractWarningKind.BelowMinShifts or ContractWarningKind.BelowMinHours);
                        }

                        warnings.AddRange(weekWarnings);
                        warnings.AddRange(ContractCompliance.ShiftsDuringTimeOff(user.Id, week, inWeek, approvedTimeOff));
                    }
                }

                rows.Add(new RotaStaffRow(
                    user,
                    primaryByUser.GetValueOrDefault(user.Id),
                    locationShiftsByUser[user.Id].ToList(),
                    hoursByWeek,
                    warnings,
                    isMember)
                {
                    TimeOff = userTimeOff
                });
            }

            rows = rows
                .OrderBy(x => x.PrimaryPosition?.StaffPosition?.SortOrder ?? int.MaxValue)
                .ThenBy(x => x.PrimaryPosition?.StaffPosition?.Name ?? string.Empty)
                .ThenBy(x => x.DisplayName)
                .ToList();

            // Counted in the database with the same predicate publish uses, so it also includes a
            // published shift that's been moved out of this range since.
            DateTime rangeStartUtc = RotaTime.StartOfDayUtc(from);
            DateTime rangeEndUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            List<string?> pendingUserIds = await PendingForRange(ctx, locationId, rangeStartUtc, rangeEndUtc)
                .AsNoTracking()
                .TagWithCallSite()
                .Select(x => x.UserId)
                .ToListAsync();

            return Result<RotaRangeView>.Ok(new RotaRangeView(
                locationId,
                companyId,
                location.Name,
                from,
                to,
                rows,
                positions,
                pendingUserIds.Count,
                pendingUserIds.Where(x => x is not null).Distinct().Count())
            {
                OpenShifts = locationShifts.Where(x => x.UserId is null && x.IsActive).ToList(),
                UnpublishedOpenShiftCount = pendingUserIds.Count(x => x is null)
            });
        }
        catch (Exception ex)
        {
            return Result<RotaRangeView>.Fail($"Failed to load the rota: {ex.Message}");
        }
    }

    public async Task<Result<ShiftSaveResult>> SaveShiftAsync(LocationScope scope, Shift shift, string actingUserId)
    {
        try
        {
            string? timeError = ValidateTimes(shift.StartUtc, shift.EndUtc, shift.BreakMinutes);

            if (timeError is not null)
            {
                return Result<ShiftSaveResult>.Fail(timeError);
            }

            if (!SchedulingAccess.CanManageLocation(scope, shift.LocationId))
            {
                return Result<ShiftSaveResult>.Fail("You can only edit the rota for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Location? location = await ctx.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == shift.LocationId && x.IsActive);

            if (location is null)
            {
                return Result<ShiftSaveResult>.Fail("Location not found.");
            }

            Shift? existing = null;

            if (shift.Id != Guid.Empty)
            {
                existing = await ctx.Shifts.FirstOrDefaultAsync(x => x.Id == shift.Id);

                if (existing is null || !existing.IsActive)
                {
                    return Result<ShiftSaveResult>.Fail("That shift no longer exists.");
                }

                if (existing.LocationId != shift.LocationId)
                {
                    return Result<ShiftSaveResult>.Fail("A shift can't be moved to a different location.");
                }
            }

            // Membership only matters when choosing who works the shift. Someone who has since left
            // the location can still have an existing shift of theirs corrected (or removed) - the
            // shift dialog deliberately keeps them selectable for exactly that.
            bool assigningPerson = shift.UserId is not null && (existing is null || existing.UserId != shift.UserId);

            if (assigningPerson)
            {
                bool isMember = await ctx.UserLocationMemberships
                    .AnyAsync(x => x.UserId == shift.UserId && x.LocationId == shift.LocationId);

                if (!isMember)
                {
                    return Result<ShiftSaveResult>.Fail("That person isn't a member of this location.");
                }
            }

            List<string> warnings = [];

            if (shift.StaffPositionId is Guid positionId)
            {
                // An archived position stays valid on a shift that already had it, so the shift can
                // still be edited; it just can't be newly chosen.
                bool keepingArchivedPosition = existing?.StaffPositionId == positionId;

                StaffPosition? position = await ctx.StaffPositions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == positionId && x.CompanyId == location.CompanyId && (x.IsActive || keepingArchivedPosition));

                if (position is null)
                {
                    return Result<ShiftSaveResult>.Fail("That position doesn't exist for this company.");
                }

                bool holdsPosition = shift.UserId is null
                    || await ctx.UserPositions.AnyAsync(x => x.UserId == shift.UserId && x.StaffPositionId == positionId);

                if (!holdsPosition && position.IsActive)
                {
                    warnings.Add($"This person doesn't normally work as {position.Name}.");
                }
            }

            // Reassigning a published shift is really "take it off one person, give it to
            // another", and both people need telling - so it becomes a removal plus a new draft.
            bool reassignPublished = existing is not null && existing.PublishedDateUtc is not null && existing.UserId != shift.UserId;
            Guid excludeId = existing is not null && !reassignPublished ? existing.Id : Guid.Empty;

            // An open shift has nobody to clash with or be on holiday.
            string? overlap = shift.UserId is null ? null : await FindOverlapAsync(ctx, shift.UserId, shift.StartUtc, shift.EndUtc, excludeId);

            if (overlap is not null)
            {
                return Result<ShiftSaveResult>.Fail(overlap);
            }

            // Time off never blocks a shift (the manager may have agreed a swap), but it's said out
            // loud so nobody is scheduled on their holiday by accident.
            DateOnly shiftDay = RotaTime.LocalDate(shift.StartUtc);
            TimeOffRequest? timeOff = shift.UserId is null
                ? null
                : await TimeOffService.LiveTimeOffQuery(ctx, [shift.UserId], shiftDay, shiftDay).FirstOrDefaultAsync();

            if (timeOff is not null)
            {
                string typeName = timeOff.TimeOffType?.Name.ToLower() ?? "time off";

                warnings.Add(timeOff.Status == TimeOffStatus.Approved
                    ? $"This person has approved {typeName} on {shiftDay.ToString("ddd d MMM", RotaFormat.Uk)}."
                    : $"This person has asked for {typeName} on {shiftDay.ToString("ddd d MMM", RotaFormat.Uk)} (not decided yet).");
            }

            DateTime now = DateTime.UtcNow;
            Shift saved;

            if (existing is null || reassignPublished)
            {
                if (existing is not null)
                {
                    existing.IsActive = false;
                    // Nobody to tell when the shift being replaced was an open one.
                    existing.RemovalPending = existing.UserId is not null;
                    existing.UpdateDate = now;
                    existing.UpdateByUserId = actingUserId;
                }

                saved = new Shift
                {
                    Id = Guid.NewGuid(),
                    LocationId = shift.LocationId,
                    UserId = shift.UserId,
                    StaffPositionId = shift.StaffPositionId,
                    StartUtc = DateTime.SpecifyKind(shift.StartUtc, DateTimeKind.Utc),
                    EndUtc = DateTime.SpecifyKind(shift.EndUtc, DateTimeKind.Utc),
                    BreakMinutes = shift.BreakMinutes,
                    Notes = NormaliseNotes(shift.Notes),
                    IsActive = true,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                };

                ctx.Shifts.Add(saved);
            }
            else
            {
                string? notes = NormaliseNotes(shift.Notes);

                bool changed = existing.UserId != shift.UserId
                    || existing.StaffPositionId != shift.StaffPositionId
                    || existing.StartUtc != shift.StartUtc
                    || existing.EndUtc != shift.EndUtc
                    || existing.BreakMinutes != shift.BreakMinutes
                    || existing.Notes != notes;

                // Only a real edit stamps UpdateDate - otherwise opening and re-saving a published
                // shift would mark it "changed" and re-notify the person for nothing.
                if (changed)
                {
                    existing.UserId = shift.UserId;
                    existing.StaffPositionId = shift.StaffPositionId;
                    existing.StartUtc = DateTime.SpecifyKind(shift.StartUtc, DateTimeKind.Utc);
                    existing.EndUtc = DateTime.SpecifyKind(shift.EndUtc, DateTimeKind.Utc);
                    existing.BreakMinutes = shift.BreakMinutes;
                    existing.Notes = notes;
                    existing.UpdateDate = now;
                    existing.UpdateByUserId = actingUserId;
                }

                saved = existing;
            }

            // Any pick-up, call-off or swap on the old version of this shift no longer matches it.
            if (existing is not null && (reassignPublished || existing.UpdateDate == now))
            {
                await ShiftClaimRules.WithdrawForShiftsAsync(ctx, [existing.Id], "A manager changed the shift.", now);
            }

            await ctx.SaveChangesAsync();

            return Result<ShiftSaveResult>.Ok(new ShiftSaveResult(saved, warnings));
        }
        catch (Exception ex)
        {
            return Result<ShiftSaveResult>.Fail($"Failed to save the shift: {ex.Message}");
        }
    }

    public async Task<Result<bool>> DeleteShiftAsync(LocationScope scope, Guid shiftId, string actingUserId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Shift? shift = await ctx.Shifts.FirstOrDefaultAsync(x => x.Id == shiftId && x.IsActive);

            if (shift is null)
            {
                return Result<bool>.Fail("That shift no longer exists.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, shift.LocationId))
            {
                return Result<bool>.Fail("You can only edit the rota for your own location.");
            }

            DateTime now = DateTime.UtcNow;

            shift.IsActive = false;
            // A published shift's person is told at the next publish; an open one has nobody to tell.
            shift.RemovalPending = shift.PublishedDateUtc is not null && shift.UserId is not null;
            shift.UpdateDate = now;
            shift.UpdateByUserId = actingUserId;

            await ShiftClaimRules.WithdrawForShiftsAsync(ctx, [shift.Id], "A manager removed the shift.", now);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail($"Failed to remove the shift: {ex.Message}");
        }
    }

    public async Task<Result<CopyRangeResult>> CopyRangeAsync(LocationScope scope, int locationId, DateOnly targetFrom, DateOnly targetTo, int offsetDays, string actingUserId)
    {
        try
        {
            if (offsetDays <= 0 || offsetDays % 7 != 0)
            {
                return Result<CopyRangeResult>.Fail("Shifts can only be copied from a whole number of weeks earlier.");
            }

            if (targetTo < targetFrom)
            {
                return Result<CopyRangeResult>.Fail("The end of the range must not be before the start.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<CopyRangeResult>.Fail("You can only edit the rota for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime sourceStartUtc = RotaTime.StartOfDayUtc(targetFrom.AddDays(-offsetDays));
            DateTime sourceEndUtc = RotaTime.StartOfDayUtc(targetTo.AddDays(1 - offsetDays));

            List<Shift> source = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.User)
                .Include(x => x.StaffPosition)
                .Where(x => x.LocationId == locationId && x.IsActive && x.StartUtc >= sourceStartUtc && x.StartUtc < sourceEndUtc)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            HashSet<string> members = (await ctx.UserLocationMemberships
                .Where(x => x.LocationId == locationId)
                .Select(x => x.UserId)
                .ToListAsync()).ToHashSet();

            // Everything the copies could collide with, in one query: the same people's active shifts
            // at any location across the target window, padded a day each side for overnight shifts.
            List<string> sourceUserIds = source.Where(x => x.UserId is not null).Select(x => x.UserId!).Distinct().ToList();
            DateTime targetStartUtc = RotaTime.StartOfDayUtc(targetFrom.AddDays(-1));
            DateTime targetEndUtc = RotaTime.StartOfDayUtc(targetTo.AddDays(2));

            List<Shift> occupied = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.IsActive && x.UserId != null && sourceUserIds.Contains(x.UserId) && x.StartUtc < targetEndUtc && x.EndUtc > targetStartUtc)
                .ToListAsync();

            List<string> skipped = [];
            List<Shift> added = [];
            DateTime now = DateTime.UtcNow;

            foreach (Shift original in source)
            {
                DateOnly startDate = RotaTime.LocalDate(original.StartUtc).AddDays(offsetDays);
                DateOnly endDate = RotaTime.LocalDate(original.EndUtc).AddDays(offsetDays);
                DateTime startUtc = RotaTime.ToUtc(startDate, RotaTime.LocalTime(original.StartUtc));
                DateTime endUtc = RotaTime.ToUtc(endDate, RotaTime.LocalTime(original.EndUtc));
                string who = RotaNames.For(original.User);
                string when = startDate.ToString("ddd d MMM", RotaFormat.Uk);

                // Open shifts copy as open shifts: nobody to check.
                if (original.UserId is not null && !members.Contains(original.UserId))
                {
                    skipped.Add($"{who} on {when}: no longer at this location.");
                    continue;
                }

                bool overlaps = original.UserId is not null && occupied.Concat(added)
                    .Any(x => x.UserId == original.UserId && x.StartUtc < endUtc && x.EndUtc > startUtc);

                if (overlaps)
                {
                    skipped.Add($"{who} on {when}: already has a shift then.");
                    continue;
                }

                added.Add(new Shift
                {
                    Id = Guid.NewGuid(),
                    LocationId = locationId,
                    UserId = original.UserId,
                    StaffPositionId = original.StaffPosition is { IsActive: true } ? original.StaffPositionId : null,
                    StartUtc = startUtc,
                    EndUtc = endUtc,
                    BreakMinutes = original.BreakMinutes,
                    Notes = original.Notes,
                    IsActive = true,
                    CreateDate = now,
                    CreateByUserId = actingUserId
                });
            }

            ctx.Shifts.AddRange(added);
            await ctx.SaveChangesAsync();

            return Result<CopyRangeResult>.Ok(new CopyRangeResult(added.Count, skipped));
        }
        catch (Exception ex)
        {
            return Result<CopyRangeResult>.Fail($"Failed to copy shifts: {ex.Message}");
        }
    }

    // The longest lead time Rota Settings allows; shifts further out than this can't be due.
    private const int MaxReminderLeadHours = 168;

    public async Task<Result<List<ShiftReminderDue>>> GetShiftsDueForReminderAsync(DateTime nowUtc)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime horizonUtc = nowUtc.AddHours(MaxReminderLeadHours);

            List<Shift> candidates = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.StaffPosition)
                // Only shifts exactly as published: one edited since (UpdateDate after the publish)
                // holds a time or person nobody has been told about yet, so it waits for the
                // republish rather than reminding anyone of an unannounced change.
                .Where(x => x.IsActive
                    && x.PublishedDateUtc != null
                    && (x.UpdateDate == null || x.UpdateDate <= x.PublishedDateUtc)
                    && x.StartUtc > nowUtc && x.StartUtc <= horizonUtc
                    && (x.ReminderSentForStartUtc == null || x.ReminderSentForStartUtc != x.StartUtc)
                    && x.UserId != null
                    && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return Result<List<ShiftReminderDue>>.Ok([]);
            }

            List<int> companyIds = candidates.Select(x => x.Location!.CompanyId).Distinct().ToList();

            // A company with no settings row gets the defaults: reminders on, 24 hours ahead.
            Dictionary<int, CompanySchedulingSettings> settingsByCompany = await ctx.CompanySchedulingSettings
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => companyIds.Contains(x.CompanyId))
                .ToDictionaryAsync(x => x.CompanyId);

            List<ShiftReminderDue> due = [];

            foreach (Shift shift in candidates)
            {
                CompanySchedulingSettings settings = settingsByCompany.GetValueOrDefault(shift.Location!.CompanyId)
                    ?? new CompanySchedulingSettings { CompanyId = shift.Location.CompanyId };

                if (!settings.SendShiftReminders)
                {
                    continue;
                }

                DateTime windowOpensUtc = shift.StartUtc.AddHours(-settings.ShiftReminderLeadHours);

                if (nowUtc < windowOpensUtc)
                {
                    continue;
                }

                due.Add(new ShiftReminderDue(shift, SendEmail: shift.PublishedDateUtc < windowOpensUtc));
            }

            return Result<List<ShiftReminderDue>>.Ok(due);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find shifts due a reminder");
            return Result<List<ShiftReminderDue>>.Fail($"Failed to find shifts due a reminder: {ex.Message}");
        }
    }

    public async Task<Result<bool>> ClaimShiftReminderAsync(Guid shiftId, DateTime startUtc)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Everything the claim depends on is in the WHERE clause of one UPDATE, so two sweeps
            // can't both win, and nothing else about the shift is written - a manager saving the
            // same shift at that moment isn't affected.
            IQueryable<Shift> claimable = ctx.Shifts.Where(x => x.Id == shiftId
                && x.IsActive
                && x.PublishedDateUtc != null
                && (x.UpdateDate == null || x.UpdateDate <= x.PublishedDateUtc)
                && x.StartUtc == startUtc
                && (x.ReminderSentForStartUtc == null || x.ReminderSentForStartUtc != startUtc));

            int claimed;

            if (ctx.Database.IsRelational())
            {
                claimed = await claimable.ExecuteUpdateAsync(x => x.SetProperty(s => s.ReminderSentForStartUtc, startUtc));
            }
            else
            {
                // The EF in-memory provider (tests) has no ExecuteUpdate.
                List<Shift> rows = await claimable.ToListAsync();
                rows.ForEach(x => x.ReminderSentForStartUtc = startUtc);
                claimed = await ctx.SaveChangesAsync();
            }

            return claimed == 1
                ? Result<bool>.Ok(true)
                : Result<bool>.Fail("Already reminded, or the shift has changed since it was found.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to claim the reminder for shift {ShiftId}", shiftId);
            return Result<bool>.Fail($"Failed to claim the reminder: {ex.Message}");
        }
    }

    internal static ShiftEmailLine ToEmailLine(Shift shift) =>
        new(RotaTime.LocalDate(shift.StartUtc), RotaFormat.TimeRange(shift.StartUtc, shift.EndUtc), shift.StaffPosition?.Name);

    public async Task<Result<PublishResult>> PublishRangeAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, string actingUserId)
    {
        try
        {
            if (to < from)
            {
                return Result<PublishResult>.Fail("The end of the range must not be before the start.");
            }

            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<PublishResult>.Fail("You can only publish the rota for your own location.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DateTime startUtc = RotaTime.StartOfDayUtc(from);
            DateTime endUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            List<Shift> pending = await PendingForRange(ctx, locationId, startUtc, endUtc)
                .Include(x => x.StaffPosition)
                .Include(x => x.Location)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            DateTime now = DateTime.UtcNow;
            List<PublishedChange> changes = [];

            // Open shifts have nobody to email; new or changed ones are announced to everyone who
            // could pick them up instead (below).
            List<Shift> opened = pending.Where(x => x.UserId is null && x.IsActive).ToList();

            foreach (Shift shift in pending.Where(x => x.UserId is null))
            {
                shift.RemovalPending = false;
                shift.PublishedDateUtc = now;
                shift.PublishedByUserId = actingUserId;
                shift.PublishedStartUtc = shift.StartUtc;
            }

            foreach (IGrouping<string, Shift> group in pending.Where(x => x.UserId is not null).GroupBy(x => x.UserId!))
            {
                List<Shift> added = [];
                List<Shift> changed = [];
                List<Shift> removed = [];

                foreach (Shift shift in group)
                {
                    switch (shift.GetState())
                    {
                        case ShiftState.Draft:
                            added.Add(shift);
                            break;
                        case ShiftState.Changed:
                            changed.Add(shift);
                            break;
                        case ShiftState.RemovedPendingNotice:
                            removed.Add(shift);
                            shift.RemovalPending = false;
                            break;
                    }

                    shift.PublishedDateUtc = now;
                    shift.PublishedByUserId = actingUserId;
                    shift.PublishedStartUtc = shift.StartUtc;
                }

                changes.Add(new PublishedChange(group.Key, added, changed, removed));
            }

            await ctx.SaveChangesAsync();

            PublishResult result = new(changes) { OpenedShifts = opened };

            // One email per person, listing only their own shifts that changed - so a last-minute
            // edit reaches just the people it touches. Queued, so the publish never waits on it.
            // The publish is already saved, so a failure here is logged, never reported as a
            // failed publish.
            try
            {
                string locationName = await ctx.Locations.AsNoTracking().TagWithCallSite()
                    .Where(x => x.Id == locationId)
                    .Select(x => x.Name)
                    .FirstOrDefaultAsync() ?? "your location";

                foreach (PublishedChange change in changes)
                {
                    _notifications.Enqueue([change.UserId], NotificationTopic.RotaChanged, new RotaChangedPayload(
                        locationId,
                        locationName,
                        change.New.Select(ToEmailLine).ToList(),
                        change.Changed.Select(ToEmailLine).ToList(),
                        change.Removed.Select(ToEmailLine).ToList()));
                }

                foreach (Shift shift in opened.Where(x => x.StartUtc > now))
                {
                    await ShiftClaimRules.AnnounceOpenShiftAsync(ctx, _notifications, shift, []);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rota published at location {LocationId} but its emails couldn't be queued", locationId);
            }

            _logger.LogInformation("Published {ShiftCount} rota changes for {PeopleAffected} people at location {LocationId} ({From} to {To}) by {UserId}",
                result.ShiftCount, result.PeopleAffected, locationId, from, to, actingUserId);

            return Result<PublishResult>.Ok(result);
        }
        catch (Exception ex)
        {
            return Result<PublishResult>.Fail($"Failed to publish the rota: {ex.Message}");
        }
    }

    public async Task<Result<List<Shift>>> GetShiftsForUserAsync(string userId, DateTime fromUtc, DateTime toUtc)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Shift> shifts = await ctx.Shifts
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.Location)
                .Include(x => x.StaffPosition)
                .Where(x => x.UserId == userId && x.IsActive && x.PublishedDateUtc != null
                    && x.EndUtc > fromUtc && x.StartUtc < toUtc)
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            return Result<List<Shift>>.Ok(shifts);
        }
        catch (Exception ex)
        {
            return Result<List<Shift>>.Fail($"Failed to load shifts: {ex.Message}");
        }
    }

    // Everything a publish of [startUtc, endUtc) at this location would act on: drafts, shifts
    // edited since publishing, and removals nobody's been told about. A shift counts if it starts
    // in the range now, or if it started in the range when it was last published - so moving a
    // published shift into another week can't hide the change from the week the person knows about.
    private static IQueryable<Shift> PendingForRange(ApplicationDbContext ctx, int locationId, DateTime startUtc, DateTime endUtc) =>
        ctx.Shifts.Where(x => x.LocationId == locationId
            && ((x.StartUtc >= startUtc && x.StartUtc < endUtc)
                || (x.PublishedStartUtc != null && x.PublishedStartUtc >= startUtc && x.PublishedStartUtc < endUtc))
            && ((x.IsActive && (x.PublishedDateUtc == null || x.UpdateDate > x.PublishedDateUtc))
                || (!x.IsActive && x.RemovalPending)));

    private static string? ValidateTimes(DateTime startUtc, DateTime endUtc, int breakMinutes)
    {
        if (endUtc <= startUtc)
        {
            return "A shift must end after it starts.";
        }

        TimeSpan length = endUtc - startUtc;

        if (length > MaxShiftLength)
        {
            return "A shift can't be longer than 24 hours.";
        }

        if (breakMinutes < 0)
        {
            return "Break length can't be negative.";
        }

        if (breakMinutes >= length.TotalMinutes)
        {
            return "The break must be shorter than the shift.";
        }

        return null;
    }

    // Overlap with any of the person's active shifts at any location - they can't be in two
    // places at once. Touching end-to-start (09:00-13:00 then 13:00-17:00) is allowed.
    private static async Task<string?> FindOverlapAsync(ApplicationDbContext ctx, string userId, DateTime startUtc, DateTime endUtc, Guid excludeShiftId)
    {
        Shift? clash = await ctx.Shifts
            .AsNoTracking()
            .Include(x => x.Location)
            .Include(x => x.User)
            .Where(x => x.IsActive && x.UserId == userId && x.Id != excludeShiftId
                && x.StartUtc < endUtc && x.EndUtc > startUtc)
            .OrderBy(x => x.StartUtc)
            .FirstOrDefaultAsync();

        if (clash is null)
        {
            return null;
        }

        DateTime localStart = RotaTime.ToVenueLocal(clash.StartUtc);
        DateTime localEnd = RotaTime.ToVenueLocal(clash.EndUtc);

        return $"{RotaNames.For(clash.User)} already has a shift {localStart.ToString("ddd d MMM, HH:mm", RotaFormat.Uk)}-{localEnd.ToString("HH:mm", RotaFormat.Uk)} at {clash.Location?.Name ?? "another location"}.";
    }

    private static string? NormaliseNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
}

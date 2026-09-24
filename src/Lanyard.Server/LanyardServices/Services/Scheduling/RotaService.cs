using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class RotaService(
    IDbContextFactory<ApplicationDbContext> factory,
    IContractRequirementService contractRequirementService,
    ILogger<RotaService> logger) : IRotaService
{
    private static readonly TimeSpan MaxShiftLength = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly IContractRequirementService _contractRequirementService = contractRequirementService;
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

            foreach (Shift shift in locationShifts)
            {
                rowUserIds.Add(shift.UserId);
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
                    && rowIds.Contains(x.UserId)
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

            List<StaffPosition> positions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.IsActive)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();

            HashSet<string> memberSet = memberIds.ToHashSet();
            List<DateOnly> weekStarts = [];

            for (DateOnly week = firstWeek; week < afterLastWeek; week = week.AddDays(7))
            {
                weekStarts.Add(week);
            }

            List<RotaStaffRow> rows = [];

            foreach (UserProfile user in users)
            {
                UserPosition? primary = memberPositions.FirstOrDefault(x => x.UserId == user.Id && x.IsPrimary);
                List<Shift> userCompanyShifts = companyShifts.Where(x => x.UserId == user.Id).ToList();
                ResolvedContract contract = contracts.TryGetValue(user.Id, out ResolvedContract? resolved) ? resolved : ResolvedContract.Empty;

                Dictionary<DateOnly, decimal> hoursByWeek = [];
                List<ContractWarning> warnings = [];

                foreach (DateOnly week in weekStarts)
                {
                    List<Shift> inWeek = userCompanyShifts
                        .Where(x => RotaTime.GetWeekStart(RotaTime.LocalDate(x.StartUtc)) == week)
                        .ToList();

                    hoursByWeek[week] = inWeek.Sum(x => x.PaidHours);

                    // Only people who still work here are judged against their contract - a leaver
                    // with an old shift on the rota isn't "missing" hours.
                    if (memberSet.Contains(user.Id))
                    {
                        warnings.AddRange(ContractCompliance.EvaluateWeek(user.Id, week, contract, inWeek));
                    }
                }

                rows.Add(new RotaStaffRow(
                    user,
                    primary,
                    locationShifts.Where(x => x.UserId == user.Id).ToList(),
                    hoursByWeek,
                    warnings,
                    memberSet.Contains(user.Id)));
            }

            rows = rows
                .OrderBy(x => x.PrimaryPosition?.StaffPosition?.SortOrder ?? int.MaxValue)
                .ThenBy(x => x.PrimaryPosition?.StaffPosition?.Name ?? string.Empty)
                .ThenBy(x => x.DisplayName)
                .ToList();

            DateTime rangeStartUtc = RotaTime.StartOfDayUtc(from);
            DateTime rangeEndUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

            List<Shift> pending = locationShifts
                .Where(x => x.StartUtc >= rangeStartUtc && x.StartUtc < rangeEndUtc && x.NeedsPublishing)
                .ToList();

            return Result<RotaRangeView>.Ok(new RotaRangeView(
                locationId,
                companyId,
                location.Name,
                from,
                to,
                rows,
                positions,
                pending.Count,
                pending.Select(x => x.UserId).Distinct().Count()));
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

            bool isMember = await ctx.UserLocationMemberships
                .AnyAsync(x => x.UserId == shift.UserId && x.LocationId == shift.LocationId);

            if (!isMember)
            {
                return Result<ShiftSaveResult>.Fail("That person isn't a member of this location.");
            }

            List<string> warnings = [];

            if (shift.StaffPositionId is Guid positionId)
            {
                StaffPosition? position = await ctx.StaffPositions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == positionId && x.IsActive && x.CompanyId == location.CompanyId);

                if (position is null)
                {
                    return Result<ShiftSaveResult>.Fail("That position doesn't exist for this company.");
                }

                bool holdsPosition = await ctx.UserPositions.AnyAsync(x => x.UserId == shift.UserId && x.StaffPositionId == positionId);

                if (!holdsPosition)
                {
                    warnings.Add($"This person doesn't normally work as {position.Name}.");
                }
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

            // Reassigning a published shift is really "take it off one person, give it to
            // another", and both people need telling - so it becomes a removal plus a new draft.
            bool reassignPublished = existing is not null && existing.PublishedDateUtc is not null && existing.UserId != shift.UserId;
            Guid excludeId = existing is not null && !reassignPublished ? existing.Id : Guid.Empty;

            string? overlap = await FindOverlapAsync(ctx, shift.UserId, shift.StartUtc, shift.EndUtc, excludeId);

            if (overlap is not null)
            {
                return Result<ShiftSaveResult>.Fail(overlap);
            }

            DateTime now = DateTime.UtcNow;
            Shift saved;

            if (existing is null || reassignPublished)
            {
                if (existing is not null)
                {
                    existing.IsActive = false;
                    existing.RemovalPending = true;
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

            shift.IsActive = false;
            shift.RemovalPending = shift.PublishedDateUtc is not null;
            shift.UpdateDate = DateTime.UtcNow;
            shift.UpdateByUserId = actingUserId;

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
                string when = startDate.ToString("ddd d MMM");

                if (!members.Contains(original.UserId))
                {
                    skipped.Add($"{who} on {when}: no longer at this location.");
                    continue;
                }

                bool overlapsBatch = added.Any(x => x.UserId == original.UserId && x.StartUtc < endUtc && x.EndUtc > startUtc);
                string? overlap = overlapsBatch ? "overlaps" : await FindOverlapAsync(ctx, original.UserId, startUtc, endUtc, Guid.Empty);

                if (overlap is not null)
                {
                    skipped.Add($"{who} on {when}: already has a shift then.");
                    continue;
                }

                Shift copy = new()
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
                };

                added.Add(copy);
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

            List<Shift> pending = await ctx.Shifts
                .Include(x => x.StaffPosition)
                .Where(x => x.LocationId == locationId
                    && x.StartUtc >= startUtc && x.StartUtc < endUtc
                    && ((x.IsActive && (x.PublishedDateUtc == null || x.UpdateDate > x.PublishedDateUtc))
                        || (!x.IsActive && x.RemovalPending)))
                .OrderBy(x => x.StartUtc)
                .ToListAsync();

            DateTime now = DateTime.UtcNow;
            List<PublishedChange> changes = [];

            foreach (IGrouping<string, Shift> group in pending.GroupBy(x => x.UserId))
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
                }

                changes.Add(new PublishedChange(group.Key, added, changed, removed));
            }

            await ctx.SaveChangesAsync();

            PublishResult result = new(changes);

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

        return $"{RotaNames.For(clash.User)} already has a shift {localStart:ddd d MMM, HH:mm}-{localEnd:HH:mm} at {clash.Location?.Name ?? "another location"}.";
    }

    private static string? NormaliseNotes(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
}

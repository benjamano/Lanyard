using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.Scheduling;

public class StaffBulkUpdateService(
    IDbContextFactory<ApplicationDbContext> factory,
    ILogger<StaffBulkUpdateService> logger) : IStaffBulkUpdateService
{
    // How many names an "X, Y and 3 others can't..." error lists before summarising.
    private const int NamesInError = 3;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly ILogger<StaffBulkUpdateService> _logger = logger;

    public async Task<Result<List<StaffRotaSummary>>> GetStaffAsync(LocationScope scope, int companyId, int locationId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckLocationAsync(ctx, scope, companyId, locationId);

            if (accessError is not null)
            {
                return Result<List<StaffRotaSummary>>.Fail(accessError);
            }

            List<string> ids = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.LocationId == locationId && x.UserId != ApplicationDbContext.SystemDeletedUserPlaceholderId)
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync();

            List<UserProfile> users = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => ids.Contains(x.Id))
                .ToListAsync();

            List<UserPosition> positions = await ctx.UserPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Include(x => x.StaffPosition)
                .Where(x => ids.Contains(x.UserId) && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
                .ToListAsync();

            List<Guid> primaryIds = positions.Where(x => x.IsPrimary).Select(x => x.StaffPositionId).Distinct().ToList();

            List<ContractRequirement> contracts = await ctx.ContractRequirements
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId
                    && ((x.StaffPositionId == null && x.UserId == null)
                        || (x.StaffPositionId != null && primaryIds.Contains(x.StaffPositionId.Value))
                        || (x.UserId != null && ids.Contains(x.UserId))))
                .ToListAsync();

            Dictionary<string, int> allowanceOverrides = (await ctx.TimeOffAllowances
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => x.CompanyId == companyId && x.UserId != null && ids.Contains(x.UserId) && x.TimeOffType!.IsActive)
                    .Select(x => x.UserId!)
                    .ToListAsync())
                .GroupBy(x => x)
                .ToDictionary(g => g.Key, g => g.Count());

            List<StaffRotaSummary> staff = users
                .Select(user =>
                {
                    List<StaffRotaPosition> held = positions
                        .Where(x => x.UserId == user.Id)
                        .OrderByDescending(x => x.IsPrimary)
                        .ThenBy(x => x.StaffPosition!.SortOrder)
                        .ThenBy(x => x.StaffPosition!.Name)
                        .Select(x => new StaffRotaPosition(x.StaffPositionId, x.StaffPosition!.Name, x.StaffPosition.ColorIndex, x.IsPrimary))
                        .ToList();

                    Guid? primary = held.FirstOrDefault(x => x.IsPrimary)?.PositionId;

                    return new StaffRotaSummary(
                        user.Id,
                        DisplayName(user),
                        held,
                        ContractRequirementService.Coalesce(contracts, primary, user.Id),
                        contracts.Any(x => x.UserId == user.Id),
                        allowanceOverrides.GetValueOrDefault(user.Id));
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Result<List<StaffRotaSummary>>.Ok(staff);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load staff for company {CompanyId}", companyId);
            return Result<List<StaffRotaSummary>>.Fail($"Failed to load staff: {ex.Message}");
        }
    }

    public async Task<Result<int>> ApplyPositionsAsync(LocationScope scope, int companyId, int locationId, List<string> userIds, BulkPositionChange change)
    {
        try
        {

            List<Guid> positionIds = change.PositionIds.Distinct().ToList();

            if (positionIds.Count == 0 && change.Mode != BulkPositionMode.Replace)
            {
                return Result<int>.Fail("Pick at least one position.");
            }

            if (change.PrimaryPositionId is Guid wantedPrimary
                && (change.Mode == BulkPositionMode.Remove || !positionIds.Contains(wantedPrimary)))
            {
                return Result<int>.Fail("The primary position must be one of the positions being given.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckLocationAsync(ctx, scope, companyId, locationId);

            if (accessError is not null)
            {
                return Result<int>.Fail(accessError);
            }

            Result<List<string>> members = await CheckMembersAsync(ctx, locationId, userIds);

            if (!members.IsSuccess)
            {
                return Result<int>.Fail(members.Error!);
            }

            List<string> ids = members.Data!;

            List<StaffPosition> companyPositions = await ctx.StaffPositions
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId)
                .ToListAsync();

            if (positionIds.Any(id => !companyPositions.Any(p => p.Id == id && p.IsActive)))
            {
                return Result<int>.Fail("One or more of the selected positions no longer exists.");
            }

            Dictionary<Guid, StaffPosition> positionById = companyPositions.ToDictionary(x => x.Id);
            List<Guid> companyPositionIds = positionById.Keys.ToList();

            // Only this company's rows: someone who also works for another company keeps those.
            List<UserPosition> existing = await ctx.UserPositions
                .TagWithCallSite()
                .Where(x => ids.Contains(x.UserId) && companyPositionIds.Contains(x.StaffPositionId))
                .ToListAsync();

            List<UserPosition> toRemove = [];
            List<UserPosition> toAdd = [];
            List<(UserPosition Row, bool IsPrimary)> primaryChanges = [];
            int changedPeople = 0;

            foreach (string userId in ids)
            {
                List<UserPosition> held = existing.Where(x => x.UserId == userId).ToList();
                HashSet<Guid> heldIds = held.Select(x => x.StaffPositionId).ToHashSet();

                HashSet<Guid> target = change.Mode switch
                {
                    BulkPositionMode.Add => [.. heldIds, .. positionIds],
                    BulkPositionMode.Remove => heldIds.Except(positionIds).ToHashSet(),
                    _ => positionIds.ToHashSet()
                };

                Guid? currentPrimary = held.FirstOrDefault(x => x.IsPrimary)?.StaffPositionId;
                Guid? newPrimary = PickPrimary(change, target, currentPrimary, positionById);

                bool changed = !target.SetEquals(heldIds) || newPrimary != currentPrimary;

                if (!changed)
                {
                    continue;
                }

                changedPeople++;

                foreach (UserPosition row in held.Where(x => !target.Contains(x.StaffPositionId)))
                {
                    toRemove.Add(row);
                }

                foreach (Guid positionId in target.Where(id => !heldIds.Contains(id)))
                {
                    toAdd.Add(new UserPosition
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        StaffPositionId = positionId,
                        IsPrimary = positionId == newPrimary,
                        CreateDate = DateTime.UtcNow
                    });
                }

                foreach (UserPosition row in held.Where(x => target.Contains(x.StaffPositionId)))
                {
                    bool shouldBePrimary = row.StaffPositionId == newPrimary;

                    if (row.IsPrimary != shouldBePrimary)
                    {
                        primaryChanges.Add((row, shouldBePrimary));
                    }
                }
            }

            if (changedPeople == 0)
            {
                return Result<int>.Ok(0);
            }

            // Two saves in one transaction: the partial unique index allows one primary per
            // person, and Postgres checks it per statement, so every old primary is cleared (and
            // every removed row deleted) before any new primary is written.
            await using IDbContextTransaction? transaction = ctx.Database.IsRelational() ? await ctx.Database.BeginTransactionAsync() : null;

            ctx.UserPositions.RemoveRange(toRemove);

            foreach ((UserPosition row, bool _) in primaryChanges.Where(x => !x.IsPrimary))
            {
                row.IsPrimary = false;
            }

            await ctx.SaveChangesAsync();

            foreach ((UserPosition row, bool _) in primaryChanges.Where(x => x.IsPrimary))
            {
                row.IsPrimary = true;
            }

            ctx.UserPositions.AddRange(toAdd);

            await ctx.SaveChangesAsync();

            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }

            _logger.LogInformation("Bulk {Mode} of {PositionCount} positions changed {ChangedCount} of {SelectedCount} staff at company {CompanyId}",
                change.Mode, positionIds.Count, changedPeople, ids.Count, companyId);

            return Result<int>.Ok(changedPeople);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bulk update positions at company {CompanyId}", companyId);
            return Result<int>.Fail($"Failed to update positions: {ex.Message}");
        }
    }

    public async Task<Result<int>> ApplyContractAsync(LocationScope scope, int companyId, int locationId, List<string> userIds, BulkContractChange change, string? updatedByUserId)
    {
        try
        {

            if (!change.ChangesAnything)
            {
                return Result<int>.Fail("Choose at least one requirement to change.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? accessError = await CheckLocationAsync(ctx, scope, companyId, locationId);

            if (accessError is not null)
            {
                return Result<int>.Fail(accessError);
            }

            Result<List<string>> members = await CheckMembersAsync(ctx, locationId, userIds);

            if (!members.IsSuccess)
            {
                return Result<int>.Fail(members.Error!);
            }

            List<string> ids = members.Data!;

            Dictionary<string, Guid> primaryByUser = (await ctx.UserPositions
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => ids.Contains(x.UserId) && x.IsPrimary && x.StaffPosition!.IsActive && x.StaffPosition.CompanyId == companyId)
                    .Select(x => new { x.UserId, x.StaffPositionId })
                    .ToListAsync())
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.First().StaffPositionId);

            List<Guid> positionIds = primaryByUser.Values.Distinct().ToList();

            List<ContractRequirement> parentRows = await ctx.ContractRequirements
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.UserId == null
                    && (x.StaffPositionId == null || positionIds.Contains(x.StaffPositionId.Value)))
                .ToListAsync();

            List<ContractRequirement> userRows = await ctx.ContractRequirements
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.UserId != null && ids.Contains(x.UserId))
                .ToListAsync();

            // The values being set are the same for everyone, so a range problem in them is
            // reported once up front; values being kept were validated when they were saved.
            string? valuesError = ContractRequirementService.Validate(new ContractRequirement
            {
                CompanyId = companyId,
                MinShiftsPerWeek = change.MinShiftsPerWeek.Apply(null),
                MinShiftLengthHours = change.MinShiftLengthHours.Apply(null),
                MinHoursPerWeek = change.MinHoursPerWeek.Apply(null),
                MaxHoursPerWeek = change.MaxHoursPerWeek.Apply(null)
            });

            if (valuesError is not null)
            {
                return Result<int>.Fail(valuesError);
            }

            Dictionary<string, string> names = await NamesAsync(ctx, ids);
            List<(string UserId, ContractRequirement Next)> planned = [];
            List<string> clashes = [];

            foreach (string userId in ids)
            {
                ContractRequirement? current = userRows.FirstOrDefault(x => x.UserId == userId);

                ContractRequirement next = new()
                {
                    CompanyId = companyId,
                    UserId = userId,
                    MinShiftsPerWeek = change.MinShiftsPerWeek.Apply(current?.MinShiftsPerWeek),
                    MinShiftLengthHours = change.MinShiftLengthHours.Apply(current?.MinShiftLengthHours),
                    MinHoursPerWeek = change.MinHoursPerWeek.Apply(current?.MinHoursPerWeek),
                    MaxHoursPerWeek = change.MaxHoursPerWeek.Apply(current?.MaxHoursPerWeek)
                };

                // Same combined check SaveTierAsync makes: a personal minimum is only valid
                // against the maximum it will actually sit under - the person's own saved one,
                // or an inherited one.
                ResolvedContract parent = ContractRequirementService.Coalesce(
                    parentRows, primaryByUser.TryGetValue(userId, out Guid p) ? p : null, null);

                decimal? effectiveMin = next.MinHoursPerWeek ?? parent.MinHoursPerWeek.Value;
                decimal? effectiveMax = next.MaxHoursPerWeek ?? parent.MaxHoursPerWeek.Value;

                if (effectiveMin is decimal min && effectiveMax is decimal max && min > max)
                {
                    clashes.Add(names.GetValueOrDefault(userId, userId));
                    continue;
                }

                planned.Add((userId, next));
            }

            if (clashes.Count > 0)
            {
                return Result<int>.Fail(
                    $"Minimum hours per week would be above the maximum for {JoinNames(clashes)}. Nobody was changed - set both values, or leave those people out.");
            }

            int changedPeople = 0;
            DateTime now = DateTime.UtcNow;

            foreach ((string userId, ContractRequirement next) in planned)
            {
                ContractRequirement? current = userRows.FirstOrDefault(x => x.UserId == userId);

                if (!next.HasAnyValue)
                {
                    if (current is not null)
                    {
                        ctx.ContractRequirements.Remove(current);
                        changedPeople++;
                    }

                    continue;
                }

                if (current is null)
                {
                    current = new ContractRequirement { Id = Guid.NewGuid(), CompanyId = companyId, UserId = userId };
                    ctx.ContractRequirements.Add(current);
                }
                else if (current.MinShiftsPerWeek == next.MinShiftsPerWeek && current.MinShiftLengthHours == next.MinShiftLengthHours
                    && current.MinHoursPerWeek == next.MinHoursPerWeek && current.MaxHoursPerWeek == next.MaxHoursPerWeek)
                {
                    continue;
                }

                current.MinShiftsPerWeek = next.MinShiftsPerWeek;
                current.MinShiftLengthHours = next.MinShiftLengthHours;
                current.MinHoursPerWeek = next.MinHoursPerWeek;
                current.MaxHoursPerWeek = next.MaxHoursPerWeek;
                current.UpdateDate = now;
                current.UpdatedByUserId = updatedByUserId;
                changedPeople++;
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Bulk contract update changed {ChangedCount} of {SelectedCount} staff at company {CompanyId}",
                changedPeople, ids.Count, companyId);

            return Result<int>.Ok(changedPeople);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bulk update contract requirements at company {CompanyId}", companyId);
            return Result<int>.Fail($"Failed to update contract requirements: {ex.Message}");
        }
    }

    public async Task<Result<int>> ApplyAllowancesAsync(LocationScope scope, int companyId, int locationId, List<string> userIds, List<BulkAllowanceChange> changes, string? updatedByUserId)
    {
        try
        {

            if (changes.Count == 0)
            {
                return Result<int>.Fail("Choose at least one allowance to change.");
            }

            if (changes.Select(x => x.TimeOffTypeId).Distinct().Count() != changes.Count)
            {
                return Result<int>.Fail("Each time-off type can only be changed once at a time.");
            }

            if (changes.Any(x => x.Mode == BulkAllowanceMode.Amount && (x.Hours < 0 || x.Hours > TimeOffPolicyService.MaxAllowanceHours)))
            {
                return Result<int>.Fail("An allowance must be between 0 and 8,784 hours (a whole year).");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Guid> typeIds = changes.Select(x => x.TimeOffTypeId).ToList();

            int typesInCompany = await ctx.TimeOffTypes
                .AsNoTracking()
                .TagWithCallSite()
                .CountAsync(x => typeIds.Contains(x.Id) && x.CompanyId == companyId);

            if (typesInCompany != typeIds.Count)
            {
                return Result<int>.Fail("That time-off type doesn't belong to this company.");
            }

            string? accessError = await CheckLocationAsync(ctx, scope, companyId, locationId);

            if (accessError is not null)
            {
                return Result<int>.Fail(accessError);
            }

            Result<List<string>> members = await CheckMembersAsync(ctx, locationId, userIds);

            if (!members.IsSuccess)
            {
                return Result<int>.Fail(members.Error!);
            }

            List<string> ids = members.Data!;

            List<TimeOffAllowance> existing = await ctx.TimeOffAllowances
                .TagWithCallSite()
                .Where(x => x.CompanyId == companyId && x.UserId != null && ids.Contains(x.UserId) && typeIds.Contains(x.TimeOffTypeId))
                .ToListAsync();

            HashSet<string> changedPeople = [];
            DateTime now = DateTime.UtcNow;

            foreach (string userId in ids)
            {
                foreach (BulkAllowanceChange change in changes)
                {
                    TimeOffAllowance? row = existing.FirstOrDefault(x => x.UserId == userId && x.TimeOffTypeId == change.TimeOffTypeId);

                    if (change.Mode == BulkAllowanceMode.Inherit)
                    {
                        if (row is not null)
                        {
                            ctx.TimeOffAllowances.Remove(row);
                            changedPeople.Add(userId);
                        }

                        continue;
                    }

                    bool unlimited = change.Mode == BulkAllowanceMode.Unlimited;
                    decimal hours = unlimited ? 0m : Math.Round(change.Hours, 2);

                    if (row is null)
                    {
                        row = new TimeOffAllowance { Id = Guid.NewGuid(), CompanyId = companyId, TimeOffTypeId = change.TimeOffTypeId, UserId = userId };
                        ctx.TimeOffAllowances.Add(row);
                    }
                    else if (row.IsUnlimited == unlimited && row.AllowanceHours == hours)
                    {
                        continue;
                    }

                    row.IsUnlimited = unlimited;
                    row.AllowanceHours = hours;
                    row.UpdateDate = now;
                    row.UpdatedByUserId = updatedByUserId;
                    changedPeople.Add(userId);
                }
            }

            await ctx.SaveChangesAsync();

            _logger.LogInformation("Bulk allowance update of {TypeCount} types changed {ChangedCount} of {SelectedCount} staff at company {CompanyId}",
                changes.Count, changedPeople.Count, ids.Count, companyId);

            return Result<int>.Ok(changedPeople.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bulk update allowances at company {CompanyId}", companyId);
            return Result<int>.Fail($"Failed to update allowances: {ex.Message}");
        }
    }

    // Who ends up primary. An explicit choice wins; otherwise a still-held primary is kept, and
    // failing that the first position by rota order - so anyone holding positions always has
    // exactly one primary, as SetUserPositionsAsync guarantees for the single-person editor.
    private static Guid? PickPrimary(BulkPositionChange change, HashSet<Guid> target, Guid? currentPrimary, Dictionary<Guid, StaffPosition> positionById)
    {
        List<Guid> active = target.Where(id => positionById.TryGetValue(id, out StaffPosition? p) && p.IsActive).ToList();

        if (active.Count == 0)
        {
            return null;
        }

        if (change.PrimaryPositionId is Guid chosen)
        {
            return chosen;
        }

        if (change.Mode == BulkPositionMode.Replace)
        {
            return change.PositionIds.First(active.Contains);
        }

        if (currentPrimary is Guid kept && active.Contains(kept))
        {
            return kept;
        }

        if (change.Mode == BulkPositionMode.Add)
        {
            return change.PositionIds.First(active.Contains);
        }

        return active
            .OrderBy(id => positionById[id].SortOrder)
            .ThenBy(id => positionById[id].Name)
            .First();
    }

    // The page works one location at a time: an Admin may pick any of the company's active
    // locations, a Manager only the one they signed in under - the same rule the rota builder
    // follows (SchedulingAccess.CanManageLocation).
    private static async Task<string?> CheckLocationAsync(ApplicationDbContext ctx, LocationScope scope, int companyId, int locationId)
    {
        if (!SchedulingAccess.CanManageCompany(scope, companyId) || !SchedulingAccess.CanManageLocation(scope, locationId))
        {
            return "You can only manage staff at your own location.";
        }

        bool locationInCompany = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.Id == locationId && x.CompanyId == companyId && x.IsActive);

        return locationInCompany ? null : "That location doesn't belong to this company.";
    }

    // Everyone selected must still be a member of the location, checked in one query - the list
    // may be stale, and a hand-built request could name anyone.
    private static async Task<Result<List<string>>> CheckMembersAsync(ApplicationDbContext ctx, int locationId, List<string> userIds)
    {
        List<string> ids = userIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return Result<List<string>>.Fail("Select at least one person.");
        }

        List<string> members = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.UserId) && x.LocationId == locationId)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync();

        int missing = ids.Count - members.Count;

        return missing == 0
            ? Result<List<string>>.Ok(ids)
            : Result<List<string>>.Fail(missing == 1
                ? "One of the selected people is no longer at this location. Refresh the list and try again."
                : $"{missing} of the selected people are no longer at this location. Refresh the list and try again.");
    }

    private static async Task<Dictionary<string, string>> NamesAsync(ApplicationDbContext ctx, List<string> ids) =>
        (await ctx.Users
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync())
        .ToDictionary(x => x.Id, DisplayName);

    private static string DisplayName(UserProfile user)
    {
        string name = user.GetName().Trim();

        return string.IsNullOrWhiteSpace(name) ? user.UserName ?? user.Email ?? "Unknown" : name;
    }

    private static string JoinNames(List<string> names)
    {
        List<string> sorted = names.Order(StringComparer.OrdinalIgnoreCase).ToList();

        if (sorted.Count <= NamesInError)
        {
            return sorted.Count == 1 ? sorted[0] : $"{string.Join(", ", sorted[..^1])} and {sorted[^1]}";
        }

        int others = sorted.Count - NamesInError;

        return $"{string.Join(", ", sorted.Take(NamesInError))} and {others} {(others == 1 ? "other" : "others")}";
    }
}

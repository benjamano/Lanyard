using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Scheduling;
using Lanyard.Infrastructure.DTO.WhatsOn;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lanyard.Application.Services.WhatsOn;

public class WhatsOnService(
    IDbContextFactory<ApplicationDbContext> factory,
    IEnumerable<IWhatsOnSource> sources,
    ILogger<WhatsOnService> logger) : IWhatsOnService
{
    private const string NoAccess = "You can only see what's on at your own location.";

    // The page asks for a day or a week; the widget for a day. Anything longer is a mistake.
    public const int MaxDays = 62;

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
    private readonly List<IWhatsOnSource> _sources = sources.ToList();
    private readonly ILogger<WhatsOnService> _logger = logger;

    public async Task<Result<WhatsOnRange>> GetRangeAsync(LocationScope scope, int locationId, DateOnly from, DateOnly to, bool includeCancelled = false)
    {
        try
        {
            if (!SchedulingAccess.CanManageLocation(scope, locationId))
            {
                return Result<WhatsOnRange>.Fail(NoAccess);
            }

            if (to < from || to.DayNumber - from.DayNumber + 1 > MaxDays)
            {
                return Result<WhatsOnRange>.Fail($"Pick a range of 1 to {MaxDays} days.");
            }

            int? companyId;
            List<WhatsOnStaff> staff;

            await using (ApplicationDbContext ctx = await _factory.CreateDbContextAsync())
            {
                companyId = await ctx.Locations
                    .AsNoTracking()
                    .TagWithCallSite()
                    .Where(x => x.Id == locationId)
                    .Select(x => (int?)x.CompanyId)
                    .FirstOrDefaultAsync();

                if (companyId is null)
                {
                    return Result<WhatsOnRange>.Fail("That location doesn't exist.");
                }

                staff = await GetStaffAsync(ctx, locationId, from, to);
            }

            List<WhatsOnItem> items = [];
            List<string> warnings = [];

            // Sources run one after another: each opens its own DbContext, and a page load is a
            // handful of small queries.
            foreach (IWhatsOnSource source in _sources)
            {
                Result<List<WhatsOnItem>> result = await source.GetItemsAsync(scope, companyId.Value, locationId, from, to, includeCancelled);

                if (result.IsSuccess && result.Data is not null)
                {
                    items.AddRange(result.Data);
                }
                else
                {
                    _logger.LogWarning("What's on source {Source} failed for {LocationId}: {Error}", source.GetType().Name, locationId, result.Error);
                    warnings.Add(result.Error ?? "Something couldn't be loaded.");
                }
            }

            return Result<WhatsOnRange>.Ok(Build(locationId, from, to, items, staff, warnings));
        }
        catch (Exception ex)
        {
            return Result<WhatsOnRange>.Fail($"Failed to load what's on: {ex.Message}");
        }
    }

    // Buckets entries and shifts into every venue-local day of the range, earliest first.
    public static WhatsOnRange Build(int locationId, DateOnly from, DateOnly to, IEnumerable<WhatsOnItem> items, IEnumerable<WhatsOnStaff> staff, List<string> warnings)
    {
        ILookup<DateOnly, WhatsOnItem> itemsByDay = items.ToLookup(x => x.LocalDate);
        ILookup<DateOnly, WhatsOnStaff> staffByDay = staff.ToLookup(x => RotaTime.LocalDate(x.StartUtc));

        List<WhatsOnDay> days = [];

        for (DateOnly day = from; day <= to; day = day.AddDays(1))
        {
            List<WhatsOnItem> dayItems = itemsByDay[day].OrderBy(x => x.StartUtc).ThenBy(x => x.Title).ToList();
            List<WhatsOnStaff> dayStaff = staffByDay[day].OrderBy(x => x.StartUtc).ThenBy(x => x.Name).ToList();

            days.Add(new WhatsOnDay(day, dayItems, dayStaff, WhatsOnTotals.For(dayItems)));
        }

        return new WhatsOnRange(locationId, from, to, days, warnings);
    }

    // The rota as staff were last told it (the Published* snapshot, like the calendar feed), so a
    // manager's unpublished draft doesn't show people who don't know they're working. Open
    // shifts have nobody on them, so they're left out.
    private static async Task<List<WhatsOnStaff>> GetStaffAsync(ApplicationDbContext ctx, int locationId, DateOnly from, DateOnly to)
    {
        DateTime fromUtc = RotaTime.StartOfDayUtc(from);
        DateTime toUtc = RotaTime.StartOfDayUtc(to.AddDays(1));

        List<Shift> shifts = await ctx.Shifts
            .AsNoTracking()
            .TagWithCallSite()
            .Include(x => x.User)
            .Where(x => x.LocationId == locationId
                && x.UserId != null
                && x.PublishedDateUtc != null
                && (x.IsActive || x.RemovalPending)
                && (x.PublishedStartUtc ?? x.StartUtc) >= fromUtc
                && (x.PublishedStartUtc ?? x.StartUtc) < toUtc)
            .ToListAsync();

        List<Guid> positionIds = shifts
            .Select(PositionId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        Dictionary<Guid, StaffPosition> positions = await ctx.StaffPositions
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => positionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        return shifts.Select(shift =>
        {
            bool hasSnapshot = shift.PublishedEndUtc is not null;
            StaffPosition? position = PositionId(shift) is Guid id && positions.TryGetValue(id, out StaffPosition? found) ? found : null;

            return new WhatsOnStaff(
                shift.UserId!,
                RotaNames.For(shift.User),
                position?.Name,
                position?.ColorIndex,
                hasSnapshot ? shift.PublishedStartUtc ?? shift.StartUtc : shift.StartUtc,
                hasSnapshot ? shift.PublishedEndUtc!.Value : shift.EndUtc);
        }).ToList();
    }

    private static Guid? PositionId(Shift shift) =>
        shift.PublishedEndUtc is null ? shift.StaffPositionId : shift.PublishedStaffPositionId;
}

using System.Collections.Concurrent;
using Lanyard.Application.Services.Features;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Parties;

public class PartyScheduleProvider(ISystemDbContextFactory factory, TimeProvider timeProvider) : IPartyScheduleProvider
{
    /// <summary>How long one location's day is reused. Bookings rarely change mid-day, and every save invalidates.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    // Rules can fire up to 120 minutes before a milestone, so a party just after midnight must be
    // visible the evening before.
    private static readonly TimeSpan LookAheadPastMidnight = TimeSpan.FromHours(2);

    private readonly ISystemDbContextFactory _factory = factory;
    private readonly TimeProvider _time = timeProvider;

    private readonly ConcurrentDictionary<(int CompanyId, int LocationId), CacheEntry> _cache = new();

    // Bumped by Invalidate. A read records the generation it started from and its result only
    // counts as fresh while that's still current, so a save landing mid-read is never lost.
    private readonly ConcurrentDictionary<int, long> _generations = new();

    private sealed record CacheEntry(DateOnly Date, long Generation, DateTimeOffset ExpiresAt, IReadOnlyList<PartySlot> Slots);

    public async Task<Result<IReadOnlyList<PartySlot>>> GetDayAsync(int companyId, int locationId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        DateOnly today = RotaTime.LocalDate(nowUtc);
        long generation = _generations.GetValueOrDefault(locationId);
        DateTimeOffset now = _time.GetUtcNow();

        if (_cache.TryGetValue((companyId, locationId), out CacheEntry? cached)
            && cached.Date == today
            && cached.Generation == generation
            && cached.ExpiresAt > now)
        {
            return Result<IReadOnlyList<PartySlot>>.Ok(cached.Slots);
        }

        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

            IReadOnlyList<PartySlot> slots = await LoadAsync(ctx, companyId, locationId, today, cancellationToken);

            _cache[(companyId, locationId)] = new CacheEntry(today, generation, now + CacheDuration, slots);

            return Result<IReadOnlyList<PartySlot>>.Ok(slots);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<IReadOnlyList<PartySlot>>.Fail($"Couldn't load today's parties: {ex.Message}");
        }
    }

    public void Invalidate(int locationId)
    {
        _generations.AddOrUpdate(locationId, 1, (_, current) => current + 1);
    }

    private static async Task<IReadOnlyList<PartySlot>> LoadAsync(ApplicationDbContext ctx, int companyId, int locationId, DateOnly today, CancellationToken cancellationToken)
    {
        // The system context has no tenant filter: the location has to be this company's, and open.
        bool locationOk = await ctx.Locations
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(l => l.Id == locationId && l.CompanyId == companyId && l.IsActive, cancellationToken);

        if (!locationOk || !await CompanyFeatureQueries.IsEnabledForCompanyAsync(ctx, companyId, CompanyFeature.Parties, cancellationToken))
        {
            return [];
        }

        DateTime dayStartUtc = RotaTime.StartOfDayUtc(today);
        DateTime windowEndUtc = RotaTime.StartOfDayUtc(today.AddDays(1)) + LookAheadPastMidnight;

        return await ctx.PartyBookings
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.CompanyId == companyId
                && x.LocationId == locationId
                && x.Status == PartyBookingStatus.Booked
                && !x.ExternalIsProvisional
                && x.HireKind == null
                && x.StartUtc < windowEndUtc
                && x.EndUtc > dayStartUtc)
            .OrderBy(x => x.StartUtc)
            .ThenBy(x => x.CreateDate)
            .Select(x => new PartySlot(x.Id, x.LocationId, x.StartUtc, x.EndUtc, x.EatTimeUtc, x.LaserTagTimeUtc, x.Room, x.ChildName, x.ChildAgeTurning, x.CreateDate))
            .ToListAsync(cancellationToken);
    }
}

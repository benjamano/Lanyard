using System.Collections.Concurrent;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Demo;

// Answers "is this the demo company / a demo user?" for the lockdowns, email suppression and the
// demo banner. Singleton with a short cache: these are asked on hot paths (every email, every
// page's layout) and the answers only change when the demo is first seeded.
public interface IDemoDirectory
{
    Task<bool> IsDemoCompanyAsync(int? companyId);

    // A user belongs to the demo if they have any membership in a demo company's locations -
    // that covers the three login accounts, the seeded staff, and anyone a visitor creates.
    Task<bool> IsDemoUserAsync(string? userId);

    // The demo location a demo account signs in to: its first membership in a demo company, or
    // null if the demo hasn't been seeded (or the account has been moved out of it).
    Task<(int LocationId, int CompanyId)?> GetLoginLocationAsync(string userId);

    void Invalidate();
}

public sealed class DemoDirectory(ISystemDbContextFactory factory, TimeProvider timeProvider) : IDemoDirectory
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (bool IsDemo, DateTimeOffset At)> _users = new();
    private (HashSet<int> Ids, DateTimeOffset At)? _demoCompanies;

    public async Task<bool> IsDemoCompanyAsync(int? companyId)
    {
        return companyId is int id && (await GetDemoCompanyIdsAsync()).Contains(id);
    }

    public async Task<bool> IsDemoUserAsync(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        if (DemoAccounts.LoginUserIds.Contains(userId))
        {
            return true;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (_users.TryGetValue(userId, out (bool IsDemo, DateTimeOffset At) cached) && now - cached.At < CacheTtl)
        {
            return cached.IsDemo;
        }

        HashSet<int> demoCompanyIds = await GetDemoCompanyIdsAsync();
        bool isDemo = false;

        if (demoCompanyIds.Count > 0)
        {
            await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

            isDemo = await ctx.UserLocationMemberships
                .AsNoTracking()
                .TagWithCallSite()
                .AnyAsync(m => m.UserId == userId && demoCompanyIds.Contains(m.Location!.CompanyId));
        }

        _users[userId] = (isDemo, now);
        return isDemo;
    }

    public async Task<(int LocationId, int CompanyId)?> GetLoginLocationAsync(string userId)
    {
        HashSet<int> demoCompanyIds = await GetDemoCompanyIdsAsync();

        if (demoCompanyIds.Count == 0)
        {
            return null;
        }

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

        var location = await ctx.UserLocationMemberships
            .AsNoTracking()
            .TagWithCallSite()
            .Where(m => m.UserId == userId && m.Location!.IsActive && demoCompanyIds.Contains(m.Location.CompanyId))
            .OrderBy(m => m.LocationId)
            .Select(m => new { m.LocationId, m.Location!.CompanyId })
            .FirstOrDefaultAsync();

        return location is null ? null : (location.LocationId, location.CompanyId);
    }

    public void Invalidate()
    {
        _demoCompanies = null;
        _users.Clear();
    }

    private async Task<HashSet<int>> GetDemoCompanyIdsAsync()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (_demoCompanies is { } cached && now - cached.At < CacheTtl)
        {
            return cached.Ids;
        }

        await using ApplicationDbContext ctx = await factory.CreateDbContextAsync();

        HashSet<int> ids = [.. await ctx.Companies
            .AsNoTracking()
            .TagWithCallSite()
            .Where(x => x.IsDemo)
            .Select(x => x.Id)
            .ToListAsync()];

        _demoCompanies = (ids, now);
        return ids;
    }
}

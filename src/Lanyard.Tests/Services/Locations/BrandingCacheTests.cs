using System.Security.Claims;
using Lanyard.App.Components.Demo;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Themes;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Locations;

// The per-company branding cache and the per-location / per-user lookups that share it: hits skip
// the database, every write that changes what they return clears them, and one company's (or one
// location's, or one user's) answer is never served for another id.
[TestClass]
public class BrandingCacheTests
{
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private MemoryCache _cache = null!;
    private int _contextsCreated;

    [TestInitialize]
    public void Setup()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _cache = new MemoryCache(new MemoryCacheOptions());
        _contextsCreated = 0;
    }

    [TestCleanup]
    public void Cleanup() => _cache.Dispose();

    // Counts contexts handed out, which is how many times a service went to the database.
    private IDbContextFactory<ApplicationDbContext> CountingFactory()
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref _contextsCreated);
                return new ApplicationDbContext(_options);
            });
        factory.Setup(f => f.CreateDbContext())
            .Returns(() =>
            {
                Interlocked.Increment(ref _contextsCreated);
                return new ApplicationDbContext(_options);
            });
        return factory.Object;
    }

    private CompanyLocationService Companies() => new(CountingFactory(), _cache);

    private async Task<Company> AddCompanyAsync(string name, string? color = null, Guid? logo = null, Guid? background = null)
    {
        await using ApplicationDbContext ctx = new(_options);
        Company company = new()
        {
            Name = name,
            IsActive = true,
            ThemeColorHex = color,
            LogoFileId = logo,
            BackgroundImageFileId = background,
        };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();
        return company;
    }

    private async Task<Location> AddLocationAsync(int companyId, string name)
    {
        await using ApplicationDbContext ctx = new(_options);
        Location location = new() { CompanyId = companyId, Name = name, IsActive = true };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();
        return location;
    }

    private async Task AddUserAsync(string userId, params int[] locationIds)
    {
        await using ApplicationDbContext ctx = new(_options);
        ctx.Users.Add(new UserProfile { Id = userId, UserName = userId, FirstName = userId });
        foreach (int locationId in locationIds)
        {
            ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = userId, LocationId = locationId, CreateDate = DateTime.UtcNow });
        }
        await ctx.SaveChangesAsync();
    }

    // Changes a row behind the services' backs, like another instance or a direct writer would.
    private async Task SetColorDirectlyAsync(int companyId, string color)
    {
        await using ApplicationDbContext ctx = new(_options);
        Company company = await ctx.Companies.SingleAsync(x => x.Id == companyId);
        company.ThemeColorHex = color;
        await ctx.SaveChangesAsync();
    }

    // --- Company branding -----------------------------------------------------------------------

    [TestMethod]
    public async Task Branding_SecondRead_IsServedFromTheCache()
    {
        Company company = await AddCompanyAsync("Play2Day", "#C8102E");
        CompanyLocationService service = Companies();

        Result<CompanyBrandingInfo> first = await service.GetCompanyBrandingAsync(company.Id);
        Result<CompanyBrandingInfo> second = await service.GetCompanyBrandingAsync(company.Id);

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.IsTrue(second.IsSuccess, second.Error);
        Assert.AreEqual("#C8102E", second.Data!.ThemeColorHex);
        Assert.AreEqual(1, _contextsCreated);
    }

    [TestMethod]
    public async Task Branding_IsSharedAcrossServiceInstances()
    {
        // Each circuit and request gets its own scoped service; the cache is what they share.
        Company company = await AddCompanyAsync("Play2Day");

        await Companies().GetCompanyBrandingAsync(company.Id);
        Result<CompanyBrandingInfo> fromAnotherCircuit = await Companies().GetCompanyBrandingAsync(company.Id);

        Assert.IsTrue(fromAnotherCircuit.IsSuccess);
        Assert.AreEqual(1, _contextsCreated);
    }

    [TestMethod]
    public async Task Branding_IsKeptPerCompany_AndNeverLeaksBetweenThem()
    {
        Guid logoA = Guid.NewGuid();
        Guid logoB = Guid.NewGuid();
        Company a = await AddCompanyAsync("Play2Day", "#111111", logoA, Guid.NewGuid());
        Company b = await AddCompanyAsync("Partyman", "#222222", logoB, null);
        CompanyLocationService service = Companies();

        // Interleaved, twice over, so both cold and warm reads are checked.
        for (int i = 0; i < 2; i++)
        {
            CompanyBrandingInfo brandingA = (await service.GetCompanyBrandingAsync(a.Id)).Data!;
            CompanyBrandingInfo brandingB = (await service.GetCompanyBrandingAsync(b.Id)).Data!;

            Assert.AreEqual(a.Id, brandingA.CompanyId);
            Assert.AreEqual("Play2Day", brandingA.Name);
            Assert.AreEqual("#111111", brandingA.ThemeColorHex);
            Assert.AreEqual(logoA, brandingA.LogoFileId);

            Assert.AreEqual(b.Id, brandingB.CompanyId);
            Assert.AreEqual("Partyman", brandingB.Name);
            Assert.AreEqual("#222222", brandingB.ThemeColorHex);
            Assert.AreEqual(logoB, brandingB.LogoFileId);
            Assert.IsNull(brandingB.BackgroundImageFileId);
        }

        Assert.AreEqual(2, _contextsCreated);
    }

    [TestMethod]
    public async Task Branding_ForAnUnknownCompany_IsNotCached()
    {
        CompanyLocationService service = Companies();

        Result<CompanyBrandingInfo> missing = await service.GetCompanyBrandingAsync(42);
        Assert.IsFalse(missing.IsSuccess);

        await using (ApplicationDbContext ctx = new(_options))
        {
            ctx.Companies.Add(new Company { Id = 42, Name = "Late arrival", IsActive = true });
            await ctx.SaveChangesAsync();
        }

        Result<CompanyBrandingInfo> found = await service.GetCompanyBrandingAsync(42);

        Assert.IsTrue(found.IsSuccess, found.Error);
        Assert.AreEqual("Late arrival", found.Data!.Name);
    }

    [TestMethod]
    public async Task Branding_OfAnInactiveCompany_FailsAndIsNotCached()
    {
        await using (ApplicationDbContext ctx = new(_options))
        {
            ctx.Companies.Add(new Company { Id = 7, Name = "Closed", IsActive = false });
            await ctx.SaveChangesAsync();
        }

        Assert.IsFalse((await Companies().GetCompanyBrandingAsync(7)).IsSuccess);
        Assert.IsFalse(_cache.TryGetValue(BrandingCache.Branding(7), out _));
    }

    [TestMethod]
    public async Task SaveCompany_ClearsTheCachedBranding()
    {
        Company company = await AddCompanyAsync("Play2Day", "#111111");
        CompanyLocationService service = Companies();
        await service.GetCompanyBrandingAsync(company.Id);

        Guid newLogo = Guid.NewGuid();
        Guid newBackground = Guid.NewGuid();
        Result<Company> saved = await service.SaveCompanyAsync(new Company
        {
            Id = company.Id,
            Name = "Play2Day Leisure",
            ThemeColorHex = "#222222",
            LogoFileId = newLogo,
            BackgroundImageFileId = newBackground,
            SeasonalThemeSetting = SeasonalThemeSetting.Off,
        });
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        CompanyBrandingInfo after = (await service.GetCompanyBrandingAsync(company.Id)).Data!;

        Assert.AreEqual("Play2Day Leisure", after.Name);
        Assert.AreEqual("#222222", after.ThemeColorHex);
        Assert.AreEqual(newLogo, after.LogoFileId);
        Assert.AreEqual(newBackground, after.BackgroundImageFileId);
        Assert.AreEqual(SeasonalThemeSetting.Off, after.SeasonalThemeSetting);
    }

    [TestMethod]
    public async Task SaveCompany_ClearingTheLogo_IsSeenStraightAway()
    {
        Company company = await AddCompanyAsync("Play2Day", "#111111", Guid.NewGuid(), Guid.NewGuid());
        CompanyLocationService service = Companies();
        await service.GetCompanyBrandingAsync(company.Id);

        await service.SaveCompanyAsync(new Company { Id = company.Id, Name = "Play2Day", ThemeColorHex = "#111111" });

        CompanyBrandingInfo after = (await service.GetCompanyBrandingAsync(company.Id)).Data!;
        Assert.IsNull(after.LogoFileId);
        Assert.IsNull(after.BackgroundImageFileId);
    }

    [TestMethod]
    public async Task SaveCompany_OnlyClearsThatCompany()
    {
        Company a = await AddCompanyAsync("Play2Day", "#111111");
        Company b = await AddCompanyAsync("Partyman", "#222222");
        CompanyLocationService service = Companies();
        await service.GetCompanyBrandingAsync(a.Id);
        await service.GetCompanyBrandingAsync(b.Id);

        await service.SaveCompanyAsync(new Company { Id = a.Id, Name = "Play2Day", ThemeColorHex = "#333333" });

        Assert.IsFalse(_cache.TryGetValue(BrandingCache.Branding(a.Id), out _));
        Assert.IsTrue(_cache.TryGetValue(BrandingCache.Branding(b.Id), out _));
    }

    [TestMethod]
    public async Task DeactivateCompany_ClearsTheCachedBranding()
    {
        Company company = await AddCompanyAsync("Play2Day");
        CompanyLocationService service = Companies();
        Assert.IsTrue((await service.GetCompanyBrandingAsync(company.Id)).IsSuccess);

        Assert.IsTrue((await service.DeactivateCompanyAsync(company.Id)).IsSuccess);

        Assert.IsFalse((await service.GetCompanyBrandingAsync(company.Id)).IsSuccess);
    }

    [TestMethod]
    public void ACompanyIdAndALocationIdWithTheSameNumber_NeverShareAnEntry()
    {
        Assert.AreNotEqual(BrandingCache.Branding(1), BrandingCache.LocationScope(1));
        Assert.AreNotEqual(BrandingCache.UserCompany("1"), BrandingCache.SeasonalOptIn("1"));
        Assert.AreNotEqual(BrandingCache.Branding(1), BrandingCache.Branding(11));
    }

    // --- Branding by location and by user -------------------------------------------------------

    [TestMethod]
    public async Task BrandingForLocation_ReturnsThatLocationsCompany_FromTheSharedCache()
    {
        Company a = await AddCompanyAsync("Play2Day", "#111111");
        Company b = await AddCompanyAsync("Partyman", "#222222");
        await AddLocationAsync(a.Id, "Ipswich");
        Location bLocation = await AddLocationAsync(b.Id, "Norwich");
        CompanyLocationService service = Companies();

        await service.GetCompanyBrandingAsync(b.Id);
        int before = _contextsCreated;

        Result<CompanyBrandingInfo> branding = await service.GetCompanyBrandingForLocationAsync(bLocation.Id);

        Assert.IsTrue(branding.IsSuccess, branding.Error);
        Assert.AreEqual("Partyman", branding.Data!.Name);
        // One lookup for the location's company; the branding itself came from the cache.
        Assert.AreEqual(before + 1, _contextsCreated);
    }

    [TestMethod]
    public async Task BrandingForUser_CachesTheUsersCompany()
    {
        Company company = await AddCompanyAsync("Play2Day");
        Location location = await AddLocationAsync(company.Id, "Ipswich");
        await AddUserAsync("amy", location.Id);
        CompanyLocationService service = Companies();

        Assert.IsTrue((await service.GetCompanyBrandingForUserAsync("amy")).IsSuccess);
        int afterFirst = _contextsCreated;
        Result<CompanyBrandingInfo> second = await service.GetCompanyBrandingForUserAsync("amy");

        Assert.AreEqual("Play2Day", second.Data!.Name);
        Assert.AreEqual(afterFirst, _contextsCreated);
    }

    [TestMethod]
    public async Task BrandingForUser_NoCompany_FailsAndIsNotCached()
    {
        await AddUserAsync("amy");
        CompanyLocationService service = Companies();

        Assert.IsFalse((await service.GetCompanyBrandingForUserAsync("amy")).IsSuccess);
        Assert.IsFalse(_cache.TryGetValue(BrandingCache.UserCompany("amy"), out _));
    }

    [TestMethod]
    public async Task BrandingForUser_TwoCompanies_FailsAndIsNotCached()
    {
        Company a = await AddCompanyAsync("Play2Day");
        Company b = await AddCompanyAsync("Partyman");
        Location aLocation = await AddLocationAsync(a.Id, "Ipswich");
        Location bLocation = await AddLocationAsync(b.Id, "Norwich");
        await AddUserAsync("amy", aLocation.Id, bLocation.Id);

        Result<CompanyBrandingInfo> result = await Companies().GetCompanyBrandingForUserAsync("amy");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "more than one company");
        Assert.IsFalse(_cache.TryGetValue(BrandingCache.UserCompany("amy"), out _));
    }

    [TestMethod]
    public async Task BrandingForUser_IsKeptPerUser()
    {
        Company a = await AddCompanyAsync("Play2Day");
        Company b = await AddCompanyAsync("Partyman");
        Location aLocation = await AddLocationAsync(a.Id, "Ipswich");
        Location bLocation = await AddLocationAsync(b.Id, "Norwich");
        await AddUserAsync("amy", aLocation.Id);
        await AddUserAsync("ben", bLocation.Id);
        CompanyLocationService service = Companies();

        for (int i = 0; i < 2; i++)
        {
            Assert.AreEqual("Play2Day", (await service.GetCompanyBrandingForUserAsync("amy")).Data!.Name);
            Assert.AreEqual("Partyman", (await service.GetCompanyBrandingForUserAsync("ben")).Data!.Name);
        }
    }

    [TestMethod]
    public async Task RemoveUserFromLocation_ClearsTheirCachedCompany()
    {
        Company company = await AddCompanyAsync("Play2Day");
        Location location = await AddLocationAsync(company.Id, "Ipswich");
        await AddUserAsync("amy", location.Id);
        CompanyLocationService service = Companies();
        Assert.IsTrue((await service.GetCompanyBrandingForUserAsync("amy")).IsSuccess);

        Assert.IsTrue((await service.RemoveUserFromLocationAsync("amy", location.Id)).IsSuccess);

        Assert.IsFalse((await service.GetCompanyBrandingForUserAsync("amy")).IsSuccess);
    }

    [TestMethod]
    public async Task AddUserToLocation_ClearsTheirCachedCompany()
    {
        Company a = await AddCompanyAsync("Play2Day");
        Company b = await AddCompanyAsync("Partyman");
        Location aLocation = await AddLocationAsync(a.Id, "Ipswich");
        Location bLocation = await AddLocationAsync(b.Id, "Norwich");
        await AddUserAsync("amy", aLocation.Id);
        CompanyLocationService service = Companies();
        Assert.IsTrue((await service.GetCompanyBrandingForUserAsync("amy")).IsSuccess);

        Assert.IsTrue((await service.AddUserToLocationAsync("amy", bLocation.Id)).IsSuccess);

        // Now in two companies, which has no single answer - rather than the cached first one.
        Assert.IsFalse((await service.GetCompanyBrandingForUserAsync("amy")).IsSuccess);
    }

    // --- Location scope -------------------------------------------------------------------------

    private static Mock<AuthenticationStateProvider> SignedIn(int locationId, bool isAdmin = false)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "amy"),
            new(LocationClaimTypes.LocationId, locationId.ToString()),
        ];
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        ClaimsPrincipal user = new(new ClaimsIdentity(claims, "test"));
        Mock<AuthenticationStateProvider> provider = new();
        provider.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(user));
        return provider;
    }

    private CurrentLocationContextService Scope(int locationId, bool isAdmin = false) =>
        new(SignedIn(locationId, isAdmin).Object, CountingFactory(), _cache);

    [TestMethod]
    public async Task LocationScope_IsSharedAcrossCircuits()
    {
        Company company = await AddCompanyAsync("Play2Day");
        Location location = await AddLocationAsync(company.Id, "Ipswich");

        Result<LocationScope> first = await Scope(location.Id).GetScopeAsync();
        Result<LocationScope> second = await Scope(location.Id).GetScopeAsync();

        Assert.IsTrue(first.IsSuccess, first.Error);
        Assert.AreEqual(company.Id, second.Data!.CompanyId);
        Assert.AreEqual("Play2Day Ipswich", second.Data.LocationDisplayName);
        Assert.AreEqual(1, _contextsCreated);
    }

    [TestMethod]
    public async Task LocationScope_IsKeptPerLocation()
    {
        Company a = await AddCompanyAsync("Play2Day");
        Company b = await AddCompanyAsync("Partyman");
        Location aLocation = await AddLocationAsync(a.Id, "Ipswich");
        Location bLocation = await AddLocationAsync(b.Id, "Norwich");

        for (int i = 0; i < 2; i++)
        {
            LocationScope aScope = (await Scope(aLocation.Id).GetScopeAsync()).Data!;
            LocationScope bScope = (await Scope(bLocation.Id).GetScopeAsync()).Data!;

            Assert.AreEqual(a.Id, aScope.CompanyId);
            Assert.AreEqual("Play2Day Ipswich", aScope.LocationDisplayName);
            Assert.AreEqual(b.Id, bScope.CompanyId);
            Assert.AreEqual("Partyman Norwich", bScope.LocationDisplayName);
        }
    }

    [TestMethod]
    public async Task RenamingALocation_ClearsItsCachedScope()
    {
        Company company = await AddCompanyAsync("Play2Day");
        Location location = await AddLocationAsync(company.Id, "Ipswich");
        await Scope(location.Id).GetScopeAsync();

        Result<Location> saved = await Companies().SaveLocationAsync(new Location { Id = location.Id, CompanyId = company.Id, Name = "Ipswich North" });
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Assert.AreEqual("Play2Day Ipswich North", (await Scope(location.Id).GetScopeAsync()).Data!.LocationDisplayName);
    }

    [TestMethod]
    public async Task RenamingACompany_ClearsItsLocationsCachedScope()
    {
        Company company = await AddCompanyAsync("Play2Day");
        Location location = await AddLocationAsync(company.Id, "Ipswich");
        await Scope(location.Id).GetScopeAsync();

        Result<Company> saved = await Companies().SaveCompanyAsync(new Company { Id = company.Id, Name = "Play2Day Leisure" });
        Assert.IsTrue(saved.IsSuccess, saved.Error);

        Assert.AreEqual("Play2Day Leisure Ipswich", (await Scope(location.Id).GetScopeAsync()).Data!.LocationDisplayName);
    }

    [TestMethod]
    public async Task DeactivatingALocation_ClearsItsCachedScope()
    {
        Company company = await AddCompanyAsync("Play2Day");
        Location location = await AddLocationAsync(company.Id, "Ipswich");
        Assert.IsTrue((await Scope(location.Id).GetScopeAsync()).IsSuccess);

        Assert.IsTrue((await Companies().DeactivateLocationAsync(location.Id)).IsSuccess);

        Assert.IsFalse((await Scope(location.Id).GetScopeAsync()).IsSuccess);
    }

    [TestMethod]
    public async Task Branding_DirectWrite_IsPickedUpOnceTheEntryExpires()
    {
        // A direct write (no eviction) is only picked up once the entry goes, which the TTL
        // guarantees; here the entry is removed by hand to stand in for its expiry.
        Company company = await AddCompanyAsync("Play2Day", "#111111");
        CompanyLocationService service = Companies();
        await service.GetCompanyBrandingAsync(company.Id);

        await SetColorDirectlyAsync(company.Id, "#999999");
        Assert.AreEqual("#111111", (await service.GetCompanyBrandingAsync(company.Id)).Data!.ThemeColorHex);

        _cache.Remove(BrandingCache.Branding(company.Id));
        Assert.AreEqual("#999999", (await service.GetCompanyBrandingAsync(company.Id)).Data!.ThemeColorHex);
        Assert.AreEqual(TimeSpan.FromMinutes(5), BrandingCache.Ttl);
    }

    // --- Seasonal opt-in ------------------------------------------------------------------------

    private SeasonalThemeService Seasons() =>
        new(CountingFactory(), TimeProvider.System, _cache, NullLogger<SeasonalThemeService>.Instance);

    [TestMethod]
    public async Task SeasonalOptIn_IsCached_AndClearedWhenChanged()
    {
        await AddUserAsync("amy");

        Assert.IsTrue((await Seasons().GetUserOptInAsync("amy")).Data);
        int afterFirst = _contextsCreated;
        Assert.IsTrue((await Seasons().GetUserOptInAsync("amy")).Data);
        Assert.AreEqual(afterFirst, _contextsCreated);

        Assert.IsTrue((await Seasons().SetUserOptInAsync("amy", false)).IsSuccess);

        Assert.IsFalse((await Seasons().GetUserOptInAsync("amy")).Data);
    }

    [TestMethod]
    public async Task SeasonalOptIn_IsKeptPerUser()
    {
        await AddUserAsync("amy");
        await AddUserAsync("ben");
        await Seasons().SetUserOptInAsync("ben", false);

        for (int i = 0; i < 2; i++)
        {
            Assert.IsTrue((await Seasons().GetUserOptInAsync("amy")).Data);
            Assert.IsFalse((await Seasons().GetUserOptInAsync("ben")).Data);
        }
    }

    // --- Demo branding --------------------------------------------------------------------------

    [TestMethod]
    public async Task DemoVisitorBranding_NeverReachesTheServerCache()
    {
        // A visitor's "try your own colours" lives in their browser (via JS) and in their own
        // circuit's DemoBrandingState - it has no way to write the shared branding.
        Company demo = await AddCompanyAsync("Starlight Leisure", "#6D28D9");
        CompanyLocationService service = Companies();
        await service.GetCompanyBrandingAsync(demo.Id);

        Mock<IJSRuntime> js = new();
        js.Setup(x => x.InvokeAsync<string?>("lanyardDemoBranding.save", It.IsAny<object?[]?>()))
            .ReturnsAsync("blob:logo");
        DemoBrandingState state = new(js.Object);
        await state.SaveAsync(new DemoBrandingState.Branding("Acme", "#00FF00", null));

        CompanyBrandingInfo shared = (await service.GetCompanyBrandingAsync(demo.Id)).Data!;
        Assert.AreEqual("Starlight Leisure", shared.Name);
        Assert.AreEqual("#6D28D9", shared.ThemeColorHex);
        Assert.AreEqual("#00FF00", state.Current!.ColorHex);
    }
}

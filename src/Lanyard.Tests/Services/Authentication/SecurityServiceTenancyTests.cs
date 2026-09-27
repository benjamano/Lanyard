using System.Security.Claims;
using Lanyard.Application.Services.Authentication;
using Lanyard.Application.Services.Email;
using Lanyard.Application.Services.Locations;
using Lanyard.Application.Services.Onboarding;
using Lanyard.Application.Services.Tenancy;
using Lanyard.Application.Services.Training;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Authentication;

// Users aren't company-filtered rows, so SecurityService is what keeps one company's Admin (the
// public demo company's included) away from another company's accounts.
[TestClass]
public class SecurityServiceTenancyTests
{
    private const int CompanyA = 1;
    private const int CompanyB = 2;
    private const int LocationA = 10;
    private const int LocationB = 20;

    private sealed class TestTenant(int companyId) : ITenantContext
    {
        public bool IsSystem => false;
        public int? CompanyId => companyId;
        public bool IsPlatformAdmin => false;
        public int? ManageableCompanyId => companyId;
        public bool CanManageCompany(int id) => id == companyId;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://test.lanyard.local/", "https://test.lanyard.local/");
    }

    private sealed record Fixture(DbContextOptions<ApplicationDbContext> Options, UserManager<UserProfile> UserManager, UserProfile CallerB, UserProfile StaffB, UserProfile AdminA);

    private static async Task<Fixture> SeedAsync()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        ServiceCollection services = new();
        services.AddSingleton(options);
        services.AddScoped(sp => new ApplicationDbContext(sp.GetRequiredService<DbContextOptions<ApplicationDbContext>>()));
        services.AddDataProtection();
        services.AddLogging();
        services.AddIdentityCore<UserProfile>(o => o.Password.RequireNonAlphanumeric = false)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();
        UserManager<UserProfile> userManager = services.BuildServiceProvider().GetRequiredService<UserManager<UserProfile>>();

        UserProfile callerB = new() { Id = "caller-b", UserName = "callerb", FirstName = "Demo", LastName = "Admin" };
        UserProfile staffB = new() { Id = "staff-b", UserName = "staffb", FirstName = "Demo", LastName = "Staff" };
        UserProfile adminA = new() { Id = "admin-a", UserName = "admina", FirstName = "Real", LastName = "Admin" };

        foreach (UserProfile user in new[] { callerB, staffB, adminA })
        {
            Assert.IsTrue((await userManager.CreateAsync(user, "Original-Pw1")).Succeeded);
        }

        await using ApplicationDbContext ctx = new(options);
        ctx.Companies.AddRange(new Company { Id = CompanyA, Name = "Play2Day" }, new Company { Id = CompanyB, Name = "Demo Co" });
        ctx.Locations.AddRange(
            new Location { Id = LocationA, CompanyId = CompanyA, Name = "Ipswich", IsActive = true },
            new Location { Id = LocationB, CompanyId = CompanyB, Name = "Demo Town", IsActive = true });
        ctx.UserLocationMemberships.AddRange(
            new UserLocationMembership { UserId = callerB.Id, LocationId = LocationB },
            new UserLocationMembership { UserId = staffB.Id, LocationId = LocationB },
            new UserLocationMembership { UserId = adminA.Id, LocationId = LocationA });
        await ctx.SaveChangesAsync();

        return new Fixture(options, userManager, callerB, staffB, adminA);
    }

    // Signed in as callerB, an Admin of company B.
    private static SecurityService ServiceFor(Fixture f)
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, f.CallerB.Id), new Claim(ClaimTypes.Role, "Admin")], "Test"));
        Mock<AuthenticationStateProvider> auth = new();
        auth.Setup(x => x.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));

        Mock<IDbContextFactory<ApplicationDbContext>> factory = new();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(f.Options));
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ApplicationDbContext(f.Options));

        // As CompanyLocationService would answer for company B: only its own location.
        Mock<ICompanyLocationService> companyLocations = new();
        companyLocations.Setup(x => x.GetLocationsAsync(It.IsAny<int?>()))
            .ReturnsAsync(Result<List<Location>>.Ok([new Location { Id = LocationB, CompanyId = CompanyB, Name = "Demo Town" }]));

        return new SecurityService(
            auth.Object,
            new CurrentUserAccessor(auth.Object),
            factory.Object,
            f.UserManager,
            new Mock<ICourseAssignmentService>().Object,
            new Mock<ICourseService>().Object,
            NullLogger<SecurityService>.Instance,
            new TestNavigationManager(),
            new Mock<IEmailService>().Object,
            companyLocations.Object,
            Options.Create(new EmailOptions()),
            new Mock<IOnboardingService>().Object,
            new TestTenant(CompanyB));
    }

    [TestMethod]
    public async Task UserLists_OnlyContainTheCallersCompany()
    {
        Fixture f = await SeedAsync();
        SecurityService service = ServiceFor(f);

        CollectionAssert.AreEquivalent(new[] { "caller-b", "staff-b" }, (await service.GetActiveUsersAsync()).Select(x => x.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "caller-b", "staff-b" }, (await service.GetAllUsersAsync()).Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task AnotherCompanysUser_CannotBeLoadedById()
    {
        Fixture f = await SeedAsync();
        SecurityService service = ServiceFor(f);

        Assert.IsFalse((await service.GetUserByIdAsync(f.AdminA.Id)).IsSuccess);
        Assert.IsTrue((await service.GetUserByIdAsync(f.StaffB.Id)).IsSuccess);
    }

    [TestMethod]
    public async Task AnotherCompanysUsersPassword_CannotBeChanged()
    {
        Fixture f = await SeedAsync();
        SecurityService service = ServiceFor(f);

        Assert.IsFalse((await service.ChangePasswordAsync(f.AdminA.Id, "Hijacked-Pw1")).IsSuccess);

        UserProfile adminA = (await f.UserManager.FindByIdAsync(f.AdminA.Id))!;
        Assert.IsTrue(await f.UserManager.CheckPasswordAsync(adminA, "Original-Pw1"));
    }

    [TestMethod]
    public async Task OwnPasswordAndOwnCompanysStaff_CanStillBeChanged()
    {
        Fixture f = await SeedAsync();
        SecurityService service = ServiceFor(f);

        Assert.IsTrue((await service.ChangePasswordAsync(f.CallerB.Id, "New-Own-Pw1")).IsSuccess);
        Assert.IsTrue((await service.ChangePasswordAsync(f.StaffB.Id, "New-Staff-Pw1")).IsSuccess);
    }

    [TestMethod]
    public async Task AnotherCompanysUser_CannotBeEditedDeletedOrUnlocked()
    {
        Fixture f = await SeedAsync();
        SecurityService service = ServiceFor(f);

        await service.UpdateUserProfileAsync(new UserProfile { Id = f.AdminA.Id, UserName = "admina", FirstName = "Hacked", LastName = "Admin" });
        Assert.IsFalse((await service.DeleteUserAsync(f.AdminA.Id)).IsSuccess);
        Assert.IsFalse((await service.UnlockUserAsync(f.AdminA.Id)).IsSuccess);

        await using ApplicationDbContext ctx = new(f.Options);
        UserProfile adminA = await ctx.Users.SingleAsync(x => x.Id == f.AdminA.Id);
        Assert.AreEqual("Real", adminA.FirstName);
    }

    [TestMethod]
    public async Task NewUsers_CannotBeCreatedInAnotherCompanysLocation()
    {
        Fixture f = await SeedAsync();
        SecurityService service = ServiceFor(f);

        Result<UserCreationResult> result = await service.CreateUserAsync(
            new UserProfile { FirstName = "Sneaky", LastName = "Hire", Email = "sneaky@example.com" }, [LocationA]);

        Assert.IsFalse(result.IsSuccess);

        await using ApplicationDbContext ctx = new(f.Options);
        Assert.IsFalse(await ctx.Users.AnyAsync(x => x.FirstName == "Sneaky"));
    }
}

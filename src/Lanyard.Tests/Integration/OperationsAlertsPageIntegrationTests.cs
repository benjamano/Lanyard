using System.Net;
using System.Net.Http.Json;
using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The Operations alerts page end to end: the real writer raises an alert through DI, the event bus
// carries it to listening pages, and the page shows it only to the people who should see it.
[TestClass]
public class OperationsAlertsPageIntegrationTests
{
    private const string Password = "Ops-Alerts-Pw1!";
    private const string PageHeading = "Operations alerts";

    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _factory = new CustomWebApplicationFactory();
        _factory.CreateClient();

        await AddUserAsync("ipswichmanager", "Manager");
        await AddUserAsync("plainstaff", "Staff");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task AddUserAsync(string userName, string role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        UserManager<UserProfile> userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserProfile>>();

        UserProfile user = new() { UserName = userName, Email = $"{userName}@example.com", FirstName = userName, LastName = "Test", EmailConfirmed = true };
        IdentityResult created = await userManager.CreateAsync(user, Password);
        Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));

        await userManager.AddToRoleAsync(user, role);

        ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = user.Id, LocationId = ApplicationDbContext.SeedIpswichLocationId });
        await ctx.SaveChangesAsync();
    }

    private async Task<string> GetPageAsAsync(string userName, string password)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new LoginDto
        {
            Username = userName,
            Password = password,
            LocationId = ApplicationDbContext.SeedIpswichLocationId
        });
        Assert.IsTrue(login.IsSuccessStatusCode, $"Login failed: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");

        HttpResponseMessage page = await client.GetAsync("/manage/alerts");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);

        return await page.Content.ReadAsStringAsync();
    }

    private async Task<List<OperationsAlertChanged>> RaiseAsync(int? locationId, string title)
    {
        IOperationsAlertWriter writer = _factory.Services.GetRequiredService<IOperationsAlertWriter>();
        IOperationsAlertEventBus bus = _factory.Services.GetRequiredService<IOperationsAlertEventBus>();

        List<OperationsAlertChanged> events = [];
        bus.OnChanged += events.Add;

        Result<OperationsAlertRaiseOutcome> raised = await writer.RaiseAsync(new OperationsAlertRaise(
            ApplicationDbContext.SeedPlay2DayCompanyId, locationId, OperationsAlertType.KioskOffline, OperationsAlertSeverity.Critical,
            $"kiosk-offline:{Guid.NewGuid():N}", title, "Stopped responding at 14:02."));

        bus.OnChanged -= events.Add;

        Assert.IsTrue(raised.IsSuccess, raised.Error);
        Assert.AreEqual(OperationsAlertRaiseOutcome.Created, raised.Data);

        return events;
    }

    [TestMethod]
    public async Task RaisingAnAlert_PublishesOnTheBusForTheRightCompanyAndLocation()
    {
        List<OperationsAlertChanged> events = await RaiseAsync(ApplicationDbContext.SeedIpswichLocationId, "Arena PC is offline");

        OperationsAlertChanged change = events.Single();
        Assert.AreEqual(ApplicationDbContext.SeedPlay2DayCompanyId, change.CompanyId);
        Assert.AreEqual(ApplicationDbContext.SeedIpswichLocationId, change.LocationId);
        Assert.AreEqual(OperationsAlertChangeKind.Raised, change.Kind);
    }

    [TestMethod]
    public async Task Admin_SeesAlertsFromEveryLocation()
    {
        await RaiseAsync(ApplicationDbContext.SeedIpswichLocationId, "Arena PC is offline");
        await RaiseAsync(ApplicationDbContext.SeedWisbechLocationId, "Bowling PC is offline");

        string html = await GetPageAsAsync("admin", CustomWebApplicationFactory.SeedAdminPassword);

        StringAssert.Contains(html, PageHeading);
        StringAssert.Contains(html, "Arena PC is offline");
        StringAssert.Contains(html, "Bowling PC is offline");
    }

    [TestMethod]
    public async Task Manager_SeesOnlyTheirOwnLocationsAlerts()
    {
        await RaiseAsync(ApplicationDbContext.SeedIpswichLocationId, "Arena PC is offline");
        await RaiseAsync(ApplicationDbContext.SeedWisbechLocationId, "Bowling PC is offline");
        await RaiseAsync(null, "Unplaced PC is offline");

        string html = await GetPageAsAsync("ipswichmanager", Password);

        StringAssert.Contains(html, "Arena PC is offline");
        Assert.IsFalse(html.Contains("Bowling PC is offline"), "A Manager saw another location's alert.");
        Assert.IsFalse(html.Contains("Unplaced PC is offline"), "A Manager saw a company-wide alert.");
    }

    [TestMethod]
    public async Task Staff_AreRefused()
    {
        await RaiseAsync(ApplicationDbContext.SeedIpswichLocationId, "Arena PC is offline");

        string html = await GetPageAsAsync("plainstaff", Password);

        Assert.IsFalse(html.Contains("Arena PC is offline"), "The page rendered alerts for a staff member.");
    }

    [TestMethod]
    public async Task AnonymousUser_IsSentToLogin()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync("/manage/alerts");

        Assert.IsTrue((int)response.StatusCode is >= 300 and < 400, $"Expected a redirect, got {(int)response.StatusCode}.");
        StringAssert.StartsWith(response.Headers.Location!.AbsolutePath, "/HandleLogin");
    }

    [TestMethod]
    public async Task Manager_IsBlockedWhenTheCompanyHasTheFeatureOff()
    {
        await RaiseAsync(ApplicationDbContext.SeedIpswichLocationId, "Arena PC is offline");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ApplicationDbContext ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ctx.CompanyFeatureSettings.Add(new CompanyFeatureSetting
            {
                CompanyId = ApplicationDbContext.SeedPlay2DayCompanyId, Feature = CompanyFeature.OperationsAlerts, IsEnabled = false
            });
            await ctx.SaveChangesAsync();
        }

        string html = await GetPageAsAsync("ipswichmanager", Password);

        Assert.IsFalse(html.Contains("Arena PC is offline"), "The page rendered with the feature switched off.");
    }
}

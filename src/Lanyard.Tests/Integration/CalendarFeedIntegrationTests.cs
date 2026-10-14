using System.Net;
using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Integration;

// The calendar feed endpoint is the one place a staff member's shifts are served with nobody signed
// in, so the anonymous route, content type, caching headers and 304s are checked through the real
// pipeline (auth middleware, routing, the rate limiter) rather than just the service.
[TestClass]
public class CalendarFeedIntegrationTests
{
    private CustomWebApplicationFactory _factory = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new CustomWebApplicationFactory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _factory.Dispose();
    }

    private async Task<int> SeedCompanyIdAsync()
    {
        await using ApplicationDbContext ctx = await _factory.Services.GetRequiredService<ISystemDbContextFactory>().CreateDbContextAsync();

        return (await ctx.Locations.SingleAsync(x => x.Id == ApplicationDbContext.SeedIpswichLocationId)).CompanyId;
    }

    private async Task<string> CreateAdminFeedAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<ApplicationDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using (ApplicationDbContext ctx = await dbFactory.CreateDbContextAsync())
        {
            if (!await ctx.UserLocationMemberships.AnyAsync(x => x.UserId == ApplicationDbContext.SeedAdminUserId && x.LocationId == ApplicationDbContext.SeedIpswichLocationId))
            {
                ctx.UserLocationMemberships.Add(new UserLocationMembership
                {
                    UserId = ApplicationDbContext.SeedAdminUserId,
                    LocationId = ApplicationDbContext.SeedIpswichLocationId,
                    CreateDate = DateTime.UtcNow
                });
                await ctx.SaveChangesAsync();
            }
        }

        Result<CreatedCalendarFeed> created = await scope.ServiceProvider.GetRequiredService<ICalendarFeedService>()
            .CreateAsync(ApplicationDbContext.SeedAdminUserId, await SeedCompanyIdAsync());

        Assert.IsTrue(created.IsSuccess, created.Error);

        return created.Data!.RawToken;
    }

    [TestMethod]
    public async Task Feed_IsServedAnonymouslyAsAPrivateCalendar()
    {
        string token = await CreateAdminFeedAsync();
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync($"/api/calendar/{token}.ics");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.IsNotNull(response.Headers.ETag);
        Assert.IsTrue(response.Headers.CacheControl?.Private);
        StringAssert.StartsWith(await response.Content.ReadAsStringAsync(), "BEGIN:VCALENDAR\r\n");
    }

    [TestMethod]
    public async Task Feed_AnswersNotModifiedForAMatchingETag()
    {
        string token = await CreateAdminFeedAsync();
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage first = await client.GetAsync($"/api/calendar/{token}.ics");

        using HttpRequestMessage again = new(HttpMethod.Get, $"/api/calendar/{token}.ics");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        HttpResponseMessage second = await client.SendAsync(again);

        Assert.AreEqual(HttpStatusCode.NotModified, second.StatusCode);
    }

    [TestMethod]
    [DataRow("not-a-real-token")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Feed_UnknownTokenIsABare404_NotALoginRedirect(string token)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await client.GetAsync($"/api/calendar/{token}.ics");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Feed_RevokedTokenIs404()
    {
        string token = await CreateAdminFeedAsync();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            Result<bool> revoked = await scope.ServiceProvider.GetRequiredService<ICalendarFeedService>()
                .RevokeAsync(ApplicationDbContext.SeedAdminUserId, await SeedCompanyIdAsync());

            Assert.IsTrue(revoked.IsSuccess, revoked.Error);
        }

        HttpResponseMessage response = await _factory.CreateClient().GetAsync($"/api/calendar/{token}.ics");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}

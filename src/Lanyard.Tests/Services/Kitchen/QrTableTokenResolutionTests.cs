using Lanyard.Application.Services.Kitchen;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Lanyard.Shared.DTO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Lanyard.Tests.Services.Kitchen;

/// <summary>
/// What a scanned table code reports about whether the venue is taking orders.
///
/// The distinction these cover is the one that matters to a customer standing at a table: "we are
/// shut" and "we could not find out" are different answers, and the second must never be
/// delivered as the first. A venue that is open, whose availability lookup happened to fail, must
/// not tell everyone who scans that it is closed.
/// </summary>
[TestClass]
public class QrTableTokenResolutionTests
{
    private static DbContextOptions<ApplicationDbContext> GetInMemoryOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static QrTableTokenService GetService(
        DbContextOptions<ApplicationDbContext> options, Result<OrderingAvailability> availability)
    {
        Mock<IDbContextFactory<ApplicationDbContext>> factoryMock = new();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(options));

        Mock<IOrderingAvailabilityService> availabilityMock = new();
        availabilityMock.Setup(a => a.GetAsync(It.IsAny<int>())).ReturnsAsync(availability);

        return new QrTableTokenService(
            factoryMock.Object, availabilityMock.Object, new Mock<ILogger<QrTableTokenService>>().Object);
    }

    private static async Task<string> SeedTableAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = new() { Name = "Play2Day", IsActive = true };
        ctx.Companies.Add(company);
        await ctx.SaveChangesAsync();

        Location location = new()
        {
            CompanyId = company.Id, Name = "Ipswich", IsActive = true, OrderingEnabled = true
        };
        ctx.Locations.Add(location);
        await ctx.SaveChangesAsync();

        QrTableToken token = new()
        {
            LocationId = location.Id, Label = "Table 4", Token = "tok-table-4", IsActive = true
        };
        ctx.QrTableTokens.Add(token);
        await ctx.SaveChangesAsync();

        return token.Token;
    }

    [TestMethod]
    public async Task WhenTheVenueIsOpen_OrderingOpenIsTrue()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        string token = await SeedTableAsync(options);

        QrTableTokenService service = GetService(
            options, Result<OrderingAvailability>.Ok(OrderingAvailability.Open));

        Result<TableResolutionDto> result = await service.ResolveAsync(token);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(true, result.Data!.OrderingOpen);
    }

    [TestMethod]
    public async Task WhenTheVenueIsClosed_OrderingOpenIsFalseAndCarriesTheReason()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        string token = await SeedTableAsync(options);

        QrTableTokenService service = GetService(options, Result<OrderingAvailability>.Ok(
            new OrderingAvailability(false, "Ordering opens again at 09:00.", null)));

        Result<TableResolutionDto> result = await service.ResolveAsync(token);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(false, result.Data!.OrderingOpen);
        Assert.AreEqual("Ordering opens again at 09:00.", result.Data.ClosedMessage);
    }

    /// <summary>
    /// The lookup failing is not a closure. Reported as null so the endpoint can answer 503 and
    /// the customer is offered "try again" rather than being told a trading venue is shut - which
    /// is what a transient database blip used to do to every table in the estate at once.
    /// </summary>
    [TestMethod]
    public async Task WhenAvailabilityCannotBeChecked_OrderingOpenIsNullRatherThanFalse()
    {
        DbContextOptions<ApplicationDbContext> options = GetInMemoryOptions();
        string token = await SeedTableAsync(options);

        QrTableTokenService service = GetService(
            options, Result<OrderingAvailability>.Fail("Couldn't check whether this venue is taking orders."));

        Result<TableResolutionDto> result = await service.ResolveAsync(token);

        // Still a resolved table: the code is valid and the venue exists.
        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsNull(result.Data!.OrderingOpen);
    }
}

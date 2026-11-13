using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.OperationsAlerts;

[TestClass]
public class OperationsAlertRetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    private static OperationsAlertRetentionHostedService Service(DbContextOptions<ApplicationDbContext> options, int days = 90) =>
        new(new SystemDbContextFactory(options), Microsoft.Extensions.Options.Options.Create(new OperationsAlertOptions { RetentionDays = days }),
            new TestClock(Now), NullLogger<OperationsAlertRetentionHostedService>.Instance);

    [TestMethod]
    public async Task Purge_DeletesResolvedAlertsPastRetentionAndKeepsOpenOnes()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await OperationsAlertTestSupport.SeedAlertAsync(options, 1, null, "old", OperationsAlertStatus.Resolved, resolvedAt: Now.AddDays(-91));
        await OperationsAlertTestSupport.SeedAlertAsync(options, 1, null, "recent", OperationsAlertStatus.Resolved, resolvedAt: Now.AddDays(-89));
        await OperationsAlertTestSupport.SeedAlertAsync(options, 1, null, "ancient-but-open", raisedAt: Now.AddDays(-400));

        Assert.AreEqual(1, await Service(options).PurgeAsync(Now));

        await using ApplicationDbContext ctx = new(options);
        CollectionAssert.AreEquivalent(new[] { "recent", "ancient-but-open" }, await ctx.OperationsAlerts.Select(x => x.DedupKey).ToListAsync());
    }

    [TestMethod]
    public async Task Purge_NeverCutsIntoTheHistoryTab()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await OperationsAlertTestSupport.SeedAlertAsync(options, 1, null, "a", OperationsAlertStatus.Resolved, resolvedAt: Now.AddDays(-30));

        Assert.AreEqual(0, await Service(options, days: 7).PurgeAsync(Now));
    }

    [TestMethod]
    public async Task Purge_IsOffWhenRetentionIsZero()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        await OperationsAlertTestSupport.SeedAlertAsync(options, 1, null, "a", OperationsAlertStatus.Resolved, resolvedAt: Now.AddDays(-365));

        Assert.AreEqual(0, await Service(options, days: 0).PurgeAsync(Now));
    }
}

using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ShiftRetentionTests
{
    [TestMethod]
    public async Task RestoreAsync_PutsBackEverythingDetachUserAsyncChanged()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        DateTime now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

        Shift future = new() { Id = Guid.NewGuid(), LocationId = 1, UserId = "leaver", CreateByUserId = "mgr", StartUtc = now.AddDays(2), EndUtc = now.AddDays(2).AddHours(8), IsActive = true, RemovalPending = false, PublishedDateUtc = now.AddDays(-1), PublishedByUserId = "mgr" };
        Shift past = new() { Id = Guid.NewGuid(), LocationId = 1, UserId = "leaver", CreateByUserId = "mgr", StartUtc = now.AddDays(-2), EndUtc = now.AddDays(-2).AddHours(8), IsActive = true };
        Shift authored = new() { Id = Guid.NewGuid(), LocationId = 1, UserId = "someone-else", CreateByUserId = "leaver", UpdateByUserId = "leaver", PublishedByUserId = "leaver", StartUtc = now.AddDays(1), EndUtc = now.AddDays(1).AddHours(4), IsActive = true };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Shifts.AddRange(future, past, authored);
            await ctx.SaveChangesAsync();
        }

        List<ShiftRetention.ShiftFieldsSnapshot> snapshot;

        await using (ApplicationDbContext ctx = new(options))
        {
            snapshot = await ShiftRetention.DetachUserAsync(ctx, "leaver", now);
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.IsFalse(await ctx.Shifts.AnyAsync(x => x.UserId == "leaver" || x.CreateByUserId == "leaver"));
            Assert.IsFalse((await ctx.Shifts.SingleAsync(x => x.Id == future.Id)).IsActive);
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            await ShiftRetention.RestoreAsync(ctx, snapshot);
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext verify = new(options);
        Shift restoredFuture = await verify.Shifts.SingleAsync(x => x.Id == future.Id);
        Assert.AreEqual("leaver", restoredFuture.UserId);
        Assert.IsTrue(restoredFuture.IsActive);
        Assert.AreEqual("mgr", restoredFuture.PublishedByUserId);
        Assert.AreEqual("leaver", (await verify.Shifts.SingleAsync(x => x.Id == past.Id)).UserId);

        Shift restoredAuthored = await verify.Shifts.SingleAsync(x => x.Id == authored.Id);
        Assert.AreEqual("leaver", restoredAuthored.CreateByUserId);
        Assert.AreEqual("leaver", restoredAuthored.UpdateByUserId);
        Assert.AreEqual("leaver", restoredAuthored.PublishedByUserId);
    }
}

using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Scheduling;

[TestClass]
public class ScheduleRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task RestoreAsync_PutsBackEverythingDetachUserAsyncChanged()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();

        Shift future = new() { Id = Guid.NewGuid(), LocationId = 1, UserId = "leaver", CreateByUserId = "mgr", StartUtc = Now.AddDays(2), EndUtc = Now.AddDays(2).AddHours(8), IsActive = true, PublishedDateUtc = Now.AddDays(-1), PublishedByUserId = "mgr" };
        Shift past = new() { Id = Guid.NewGuid(), LocationId = 1, UserId = "leaver", CreateByUserId = "mgr", StartUtc = Now.AddDays(-2), EndUtc = Now.AddDays(-2).AddHours(8), IsActive = true };
        Shift authored = new() { Id = Guid.NewGuid(), LocationId = 1, UserId = "someone-else", CreateByUserId = "leaver", UpdateByUserId = "leaver", PublishedByUserId = "leaver", StartUtc = Now.AddDays(1), EndUtc = Now.AddDays(1).AddHours(4), IsActive = true };
        TimeEntry open = new() { Id = Guid.NewGuid(), UserId = "leaver", LocationId = 1, ClockInUtc = Now.AddHours(-3), ClockInMethod = ClockMethod.Pin, IsActive = true };
        TimeEntry approvedByLeaver = new() { Id = Guid.NewGuid(), UserId = "someone-else", LocationId = 1, ClockInUtc = Now.AddDays(-1), ClockOutUtc = Now.AddDays(-1).AddHours(4), ApprovedByUserId = "leaver", IsActive = true };
        ClockInTerminal terminal = new() { Id = Guid.NewGuid(), LocationId = 1, Name = "Tablet", DeviceTokenHash = "hash", CreatedByUserId = "leaver" };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.Shifts.AddRange(future, past, authored);
            ctx.TimeEntries.AddRange(open, approvedByLeaver);
            ctx.ClockInTerminals.Add(terminal);
            await ctx.SaveChangesAsync();
        }

        ScheduleRetention.Snapshot snapshot;

        await using (ApplicationDbContext ctx = new(options))
        {
            snapshot = await ScheduleRetention.DetachUserAsync(ctx, "leaver", Now);
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.IsFalse(await ctx.Shifts.AnyAsync(x => x.UserId == "leaver" || x.CreateByUserId == "leaver"));
            Assert.IsFalse((await ctx.Shifts.SingleAsync(x => x.Id == future.Id)).IsActive);

            TimeEntry closed = await ctx.TimeEntries.SingleAsync(x => x.Id == open.Id);
            Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, closed.UserId);
            Assert.AreEqual(Now, closed.ClockOutUtc);
            Assert.IsTrue(closed.NeedsReview);
            Assert.IsNull((await ctx.TimeEntries.SingleAsync(x => x.Id == approvedByLeaver.Id)).ApprovedByUserId);
            Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, (await ctx.ClockInTerminals.SingleAsync()).CreatedByUserId);
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            await ScheduleRetention.RestoreAsync(ctx, snapshot);
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
        Assert.AreEqual("leaver", restoredAuthored.PublishedByUserId);

        TimeEntry reopened = await verify.TimeEntries.SingleAsync(x => x.Id == open.Id);
        Assert.AreEqual("leaver", reopened.UserId);
        Assert.IsNull(reopened.ClockOutUtc);
        Assert.IsFalse(reopened.NeedsReview);
        Assert.AreEqual("leaver", (await verify.TimeEntries.SingleAsync(x => x.Id == approvedByLeaver.Id)).ApprovedByUserId);
        Assert.AreEqual("leaver", (await verify.ClockInTerminals.SingleAsync()).CreatedByUserId);
    }

    [TestMethod]
    public async Task DetachUserAsync_ClearsTimesheetNotesOnlyForAGdprErasure()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        TimeEntry deleted = new() { Id = Guid.NewGuid(), UserId = "leaver", LocationId = 1, ClockInUtc = Now.AddDays(-1), ClockOutUtc = Now.AddDays(-1).AddHours(4), Notes = "Left early - appointment", IsActive = true };
        TimeEntry erased = new() { Id = Guid.NewGuid(), UserId = "erased", LocationId = 1, ClockInUtc = Now.AddDays(-1), ClockOutUtc = Now.AddDays(-1).AddHours(4), Notes = "Left early - appointment", IsActive = true };

        await using (ApplicationDbContext ctx = new(options))
        {
            ctx.TimeEntries.AddRange(deleted, erased);
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            await ScheduleRetention.DetachUserAsync(ctx, "leaver", Now);
            await ScheduleRetention.DetachUserAsync(ctx, "erased", Now, gdprErasure: true);
            await ctx.SaveChangesAsync();
        }

        await using ApplicationDbContext verify = new(options);
        TimeEntry keptNotes = await verify.TimeEntries.SingleAsync(x => x.Id == deleted.Id);
        TimeEntry noNotes = await verify.TimeEntries.SingleAsync(x => x.Id == erased.Id);

        Assert.AreEqual("Left early - appointment", keptNotes.Notes);
        Assert.IsNull(noNotes.Notes);
        Assert.AreEqual(ApplicationDbContext.SystemDeletedUserPlaceholderId, noNotes.UserId);
        Assert.AreEqual(4, noNotes.ClockedHours, "The hours stay for payroll");
    }
}

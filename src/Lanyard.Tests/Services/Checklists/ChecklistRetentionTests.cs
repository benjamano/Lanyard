using Lanyard.Application.Services.Scheduling;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Tests.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lanyard.Tests.Services.Checklists;

// Checklist ticks are kept for six years like time entries, so deleting an account moves them to
// the placeholder (ScheduleRetention) rather than deleting them.
[TestClass]
public class ChecklistRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private const string Placeholder = ApplicationDbContext.SystemDeletedUserPlaceholderId;

    private sealed record Seeded(Guid Ticked, Guid UndoneByLeaver, Guid Untouched, Guid ChecklistId, Guid TaskId);

    private static async Task<Seeded> SeedAsync(DbContextOptions<ApplicationDbContext> options)
    {
        Checklist checklist = new()
        {
            Id = Guid.NewGuid(),
            CompanyId = 1,
            LocationId = 1,
            Name = "Opening",
            CreateByUserId = "leaver",
            UpdateByUserId = "mgr"
        };
        ChecklistTask task = new() { Id = Guid.NewGuid(), ChecklistId = checklist.Id, Title = "Fridge", CreateByUserId = "mgr", UpdateByUserId = "leaver" };

        ChecklistRun run = new() { Id = Guid.NewGuid(), CompanyId = 1, LocationId = 1, ChecklistId = checklist.Id, Name = "Opening", VenueDate = new DateOnly(2026, 10, 3) };
        ChecklistRunItem first = new() { Id = Guid.NewGuid(), RunId = run.Id, Title = "Fridge", Response = ChecklistResponse.Number };
        ChecklistRunItem second = new() { Id = Guid.NewGuid(), RunId = run.Id, Title = "Lights" };
        ChecklistRunItem third = new() { Id = Guid.NewGuid(), RunId = run.Id, Title = "Tills" };

        ChecklistCompletion ticked = new() { Id = Guid.NewGuid(), RunItemId = first.Id, UserId = "leaver", Note = "Door seal loose", Reading = 4.5m, CompletedUtc = Now.AddDays(-2) };
        ChecklistCompletion undone = new() { Id = Guid.NewGuid(), RunItemId = second.Id, UserId = "someone-else", IsActive = false, UndoneByUserId = "leaver", UndoneUtc = Now.AddDays(-2) };
        ChecklistCompletion untouched = new() { Id = Guid.NewGuid(), RunItemId = third.Id, UserId = "someone-else", Note = "All fine" };

        await using ApplicationDbContext ctx = new(options);
        ctx.Checklists.Add(checklist);
        ctx.ChecklistTasks.Add(task);
        ctx.ChecklistRuns.Add(run);
        ctx.ChecklistRunItems.AddRange(first, second, third);
        ctx.ChecklistCompletions.AddRange(ticked, undone, untouched);
        await ctx.SaveChangesAsync();

        return new Seeded(ticked.Id, undone.Id, untouched.Id, checklist.Id, task.Id);
    }

    [TestMethod]
    public async Task DetachUserAsync_MovesTicksToThePlaceholder_AndClearsNotesOnlyForAGdprErasure()
    {
        foreach (bool gdpr in new[] { false, true })
        {
            DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
            Seeded seeded = await SeedAsync(options);

            await using (ApplicationDbContext ctx = new(options))
            {
                await ScheduleRetention.DetachUserAsync(ctx, "leaver", Now, gdprErasure: gdpr);
                await ctx.SaveChangesAsync();
            }

            await using (ApplicationDbContext ctx = new(options))
            {
                ChecklistCompletion ticked = await ctx.ChecklistCompletions.SingleAsync(x => x.Id == seeded.Ticked);
                Assert.AreEqual(Placeholder, ticked.UserId);
                Assert.AreEqual(4.5m, ticked.Reading, "A reading isn't about the person, so it stays.");
                Assert.AreEqual(gdpr ? null : "Door seal loose", ticked.Note);

                Assert.IsNull((await ctx.ChecklistCompletions.SingleAsync(x => x.Id == seeded.UndoneByLeaver)).UndoneByUserId);

                ChecklistCompletion untouched = await ctx.ChecklistCompletions.SingleAsync(x => x.Id == seeded.Untouched);
                Assert.AreEqual("someone-else", untouched.UserId);
                Assert.AreEqual("All fine", untouched.Note);

                Checklist checklist = await ctx.Checklists.SingleAsync();
                Assert.IsNull(checklist.CreateByUserId);
                Assert.AreEqual("mgr", checklist.UpdateByUserId);

                ChecklistTask task = await ctx.ChecklistTasks.SingleAsync();
                Assert.AreEqual("mgr", task.CreateByUserId);
                Assert.IsNull(task.UpdateByUserId);
            }
        }
    }

    [TestMethod]
    public async Task RestoreAsync_PutsTheTicksAndAuditBack()
    {
        DbContextOptions<ApplicationDbContext> options = SchedulingTestHelpers.GetInMemoryOptions();
        Seeded seeded = await SeedAsync(options);
        ScheduleRetention.Snapshot snapshot;

        await using (ApplicationDbContext ctx = new(options))
        {
            snapshot = await ScheduleRetention.DetachUserAsync(ctx, "leaver", Now);
            await ctx.SaveChangesAsync();
        }

        Assert.AreEqual(2, snapshot.ChecklistCompletions.Count);
        Assert.AreEqual(2, snapshot.ChecklistAudit.Count);

        await using (ApplicationDbContext ctx = new(options))
        {
            await ScheduleRetention.RestoreAsync(ctx, snapshot);
            await ctx.SaveChangesAsync();
        }

        await using (ApplicationDbContext ctx = new(options))
        {
            Assert.AreEqual("leaver", (await ctx.ChecklistCompletions.SingleAsync(x => x.Id == seeded.Ticked)).UserId);
            Assert.AreEqual("leaver", (await ctx.ChecklistCompletions.SingleAsync(x => x.Id == seeded.UndoneByLeaver)).UndoneByUserId);
            Assert.AreEqual("leaver", (await ctx.Checklists.SingleAsync()).CreateByUserId);
            Assert.AreEqual("leaver", (await ctx.ChecklistTasks.SingleAsync()).UpdateByUserId);
        }
    }
}

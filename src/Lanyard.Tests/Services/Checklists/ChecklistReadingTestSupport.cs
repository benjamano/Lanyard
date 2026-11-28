using Lanyard.Application.Services.OperationsAlerts;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Tests.Services.Checklists;

// Records what the checklist tick paths ask the alert writer to do, so the tests can check the
// raise and resolve calls without the writer's own rules (feature switch, demo companies) in the way.
internal sealed class RecordingAlertWriter : IOperationsAlertWriter
{
    public List<OperationsAlertRaise> Raised { get; } = [];
    public List<(int CompanyId, string Key, string? Note)> Resolved { get; } = [];

    // When set, every call fails, as a writer that can't reach the database would.
    public bool Fail { get; set; }

    public Task<Result<OperationsAlertRaiseOutcome>> RaiseAsync(OperationsAlertRaise raise, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("The alert writer is down.");
        }

        Raised.Add(raise);
        return Task.FromResult(Result<OperationsAlertRaiseOutcome>.Ok(OperationsAlertRaiseOutcome.Created));
    }

    public Task<Result<bool>> ResolveAsync(int companyId, string dedupKey, string? note = null, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            return Task.FromResult(Result<bool>.Fail("The alert writer is down."));
        }

        Resolved.Add((companyId, dedupKey, note));
        return Task.FromResult(Result<bool>.Ok(true));
    }
}

internal static class ChecklistReadingTestSupport
{
    // Turns a task into a Number reading with a range, before the day's run copies it.
    public static async Task SetRangeAsync(DbContextOptions<ApplicationDbContext> options, string title, decimal? min, decimal? max,
        ChecklistReadingAlert alert = ChecklistReadingAlert.AlertAndNotify, string unit = "°C")
    {
        await using ApplicationDbContext ctx = new(options);

        ChecklistTask task = await ctx.ChecklistTasks.IgnoreQueryFilters().SingleAsync(x => x.Title == title);
        task.Response = ChecklistResponse.Number;
        task.Unit = unit;
        task.MinReading = min;
        task.MaxReading = max;
        task.ReadingAlert = alert;

        await ctx.SaveChangesAsync();
    }

    public static string KeyFor(Guid runItemId) => $"checklist-reading:{runItemId:N}";
}

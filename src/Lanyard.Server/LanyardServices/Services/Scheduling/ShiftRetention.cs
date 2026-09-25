using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// What happens to someone's shifts when their account is deleted (plain delete or GDPR erasure).
// Shift history is retained for payroll/employment records (docs/DATA_RETENTION.md: 6 years), so
// the rows stay but stop pointing at the person: they are re-pointed to the seeded placeholder
// account, which is also what lets Shift.UserId keep its Restrict FK. Future shifts are cancelled
// outright - there is no one to work them and no one left to notify.
//
// DetachUserAsync runs before the user row is deleted, on a context the caller saves, and returns
// a snapshot of every field it touched. The account itself is deleted by Identity's UserManager on
// its own context, so the two can't share a transaction; if that delete fails, the caller hands
// the snapshot to RestoreAsync so a failed delete leaves the person's shifts exactly as they were.
public static class ShiftRetention
{
    public record ShiftFieldsSnapshot(
        Guid ShiftId, string UserId, bool IsActive, bool RemovalPending,
        string CreateByUserId, string? UpdateByUserId, string? PublishedByUserId);

    public static async Task<List<ShiftFieldsSnapshot>> DetachUserAsync(ApplicationDbContext ctx, string userId, DateTime nowUtc)
    {
        List<Shift> affected = await ctx.Shifts
            .Where(x => x.UserId == userId || x.CreateByUserId == userId || x.UpdateByUserId == userId || x.PublishedByUserId == userId)
            .ToListAsync();

        List<ShiftFieldsSnapshot> snapshot = affected
            .Select(x => new ShiftFieldsSnapshot(x.Id, x.UserId, x.IsActive, x.RemovalPending, x.CreateByUserId, x.UpdateByUserId, x.PublishedByUserId))
            .ToList();

        foreach (Shift shift in affected)
        {
            if (shift.UserId == userId)
            {
                if (shift.StartUtc > nowUtc)
                {
                    shift.IsActive = false;
                    shift.RemovalPending = false;
                }

                shift.UserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
            }

            if (shift.CreateByUserId == userId)
            {
                shift.CreateByUserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
            }

            if (shift.UpdateByUserId == userId)
            {
                shift.UpdateByUserId = null;
            }

            if (shift.PublishedByUserId == userId)
            {
                shift.PublishedByUserId = null;
            }
        }

        return snapshot;
    }

    public static async Task RestoreAsync(ApplicationDbContext ctx, IReadOnlyCollection<ShiftFieldsSnapshot> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        List<Guid> ids = snapshot.Select(x => x.ShiftId).ToList();
        Dictionary<Guid, Shift> shifts = await ctx.Shifts.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (ShiftFieldsSnapshot original in snapshot)
        {
            if (!shifts.TryGetValue(original.ShiftId, out Shift? shift))
            {
                continue;
            }

            shift.UserId = original.UserId;
            shift.IsActive = original.IsActive;
            shift.RemovalPending = original.RemovalPending;
            shift.CreateByUserId = original.CreateByUserId;
            shift.UpdateByUserId = original.UpdateByUserId;
            shift.PublishedByUserId = original.PublishedByUserId;
        }
    }
}

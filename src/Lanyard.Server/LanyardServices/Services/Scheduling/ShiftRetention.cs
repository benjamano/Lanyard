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
// Called before the user row is deleted, on a context the caller saves.
internal static class ShiftRetention
{
    public static async Task DetachUserAsync(ApplicationDbContext ctx, string userId, DateTime nowUtc)
    {
        List<Shift> worked = await ctx.Shifts
            .Where(x => x.UserId == userId)
            .ToListAsync();

        foreach (Shift shift in worked)
        {
            if (shift.StartUtc > nowUtc)
            {
                shift.IsActive = false;
                shift.RemovalPending = false;
            }

            shift.UserId = ApplicationDbContext.SystemDeletedUserPlaceholderId;
        }

        List<Shift> attributed = await ctx.Shifts
            .Where(x => x.CreateByUserId == userId || x.UpdateByUserId == userId || x.PublishedByUserId == userId)
            .ToListAsync();

        foreach (Shift shift in attributed)
        {
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
    }
}

using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Scheduling;

// What happens to someone's rota and timesheet history when their account is deleted (plain
// delete or GDPR erasure). Both are retained for payroll/employment records
// (docs/DATA_RETENTION.md: 6 years), so the rows stay but stop pointing at the person: they are
// re-pointed to the seeded placeholder account, which is also what lets Shift.UserId and
// TimeEntry.UserId keep Restrict FKs. Future shifts are cancelled outright - there is no one to
// work them and no one left to notify - and an open time entry is closed, both because nobody is
// on the clock any more and because several deleted people's open entries under the one
// placeholder id would collide on the one-open-entry-per-person index.
//
// DetachUserAsync runs before the user row is deleted, on a context the caller saves, and returns
// a snapshot of every field it touched. The account itself is deleted by Identity's UserManager on
// its own context, so the two can't share a transaction; if that delete fails, the caller hands
// the snapshot to RestoreAsync so a failed delete leaves the person's history exactly as it was.
public static class ScheduleRetention
{
    public record ShiftFields(
        Guid Id, string UserId, bool IsActive, bool RemovalPending,
        string CreateByUserId, string? UpdateByUserId, string? PublishedByUserId);

    public record TimeEntryFields(
        Guid Id, string UserId, DateTime? ClockOutUtc, ClockMethod? ClockOutMethod, bool NeedsReview, string? ReviewReason,
        string? CreateByUserId, string? UpdateByUserId, string? ApprovedByUserId);

    public record TerminalFields(Guid Id, string CreatedByUserId, string? RevokedByUserId);

    public record Snapshot(List<ShiftFields> Shifts, List<TimeEntryFields> TimeEntries, List<TerminalFields> Terminals)
    {
        public int Count => Shifts.Count + TimeEntries.Count + Terminals.Count;
    }

    private const string Placeholder = ApplicationDbContext.SystemDeletedUserPlaceholderId;

    public static async Task<Snapshot> DetachUserAsync(ApplicationDbContext ctx, string userId, DateTime nowUtc)
    {
        List<Shift> shifts = await ctx.Shifts
            .Where(x => x.UserId == userId || x.CreateByUserId == userId || x.UpdateByUserId == userId || x.PublishedByUserId == userId)
            .ToListAsync();

        List<TimeEntry> entries = await ctx.TimeEntries
            .Where(x => x.UserId == userId || x.CreateByUserId == userId || x.UpdateByUserId == userId || x.ApprovedByUserId == userId)
            .ToListAsync();

        List<ClockInTerminal> terminals = await ctx.ClockInTerminals
            .Where(x => x.CreatedByUserId == userId || x.RevokedByUserId == userId)
            .ToListAsync();

        Snapshot snapshot = new(
            shifts.Select(x => new ShiftFields(x.Id, x.UserId, x.IsActive, x.RemovalPending, x.CreateByUserId, x.UpdateByUserId, x.PublishedByUserId)).ToList(),
            entries.Select(x => new TimeEntryFields(x.Id, x.UserId, x.ClockOutUtc, x.ClockOutMethod, x.NeedsReview, x.ReviewReason, x.CreateByUserId, x.UpdateByUserId, x.ApprovedByUserId)).ToList(),
            terminals.Select(x => new TerminalFields(x.Id, x.CreatedByUserId, x.RevokedByUserId)).ToList());

        foreach (Shift shift in shifts)
        {
            if (shift.UserId == userId)
            {
                if (shift.StartUtc > nowUtc)
                {
                    shift.IsActive = false;
                    shift.RemovalPending = false;
                }

                shift.UserId = Placeholder;
            }

            if (shift.CreateByUserId == userId)
            {
                shift.CreateByUserId = Placeholder;
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

        foreach (TimeEntry entry in entries)
        {
            if (entry.UserId == userId)
            {
                if (entry.IsOpen && entry.IsActive)
                {
                    entry.ClockOutUtc = nowUtc > entry.ClockInUtc ? nowUtc : entry.ClockInUtc;
                    entry.ClockOutMethod = ClockMethod.Automatic;
                    entry.NeedsReview = true;
                    entry.ReviewReason = "Closed automatically because the account was deleted while clocked in.";
                }

                entry.UserId = Placeholder;
            }

            if (entry.CreateByUserId == userId)
            {
                entry.CreateByUserId = null;
            }

            if (entry.UpdateByUserId == userId)
            {
                entry.UpdateByUserId = null;
            }

            if (entry.ApprovedByUserId == userId)
            {
                entry.ApprovedByUserId = null;
            }
        }

        foreach (ClockInTerminal terminal in terminals)
        {
            if (terminal.CreatedByUserId == userId)
            {
                terminal.CreatedByUserId = Placeholder;
            }

            if (terminal.RevokedByUserId == userId)
            {
                terminal.RevokedByUserId = null;
            }
        }

        return snapshot;
    }

    public static async Task RestoreAsync(ApplicationDbContext ctx, Snapshot snapshot)
    {
        if (snapshot.Count == 0)
        {
            return;
        }

        List<Guid> shiftIds = snapshot.Shifts.Select(x => x.Id).ToList();
        Dictionary<Guid, Shift> shifts = await ctx.Shifts.Where(x => shiftIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (ShiftFields original in snapshot.Shifts)
        {
            if (shifts.TryGetValue(original.Id, out Shift? shift))
            {
                shift.UserId = original.UserId;
                shift.IsActive = original.IsActive;
                shift.RemovalPending = original.RemovalPending;
                shift.CreateByUserId = original.CreateByUserId;
                shift.UpdateByUserId = original.UpdateByUserId;
                shift.PublishedByUserId = original.PublishedByUserId;
            }
        }

        List<Guid> entryIds = snapshot.TimeEntries.Select(x => x.Id).ToList();
        Dictionary<Guid, TimeEntry> entries = await ctx.TimeEntries.Where(x => entryIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (TimeEntryFields original in snapshot.TimeEntries)
        {
            if (entries.TryGetValue(original.Id, out TimeEntry? entry))
            {
                entry.UserId = original.UserId;
                entry.ClockOutUtc = original.ClockOutUtc;
                entry.ClockOutMethod = original.ClockOutMethod;
                entry.NeedsReview = original.NeedsReview;
                entry.ReviewReason = original.ReviewReason;
                entry.CreateByUserId = original.CreateByUserId;
                entry.UpdateByUserId = original.UpdateByUserId;
                entry.ApprovedByUserId = original.ApprovedByUserId;
            }
        }

        List<Guid> terminalIds = snapshot.Terminals.Select(x => x.Id).ToList();
        Dictionary<Guid, ClockInTerminal> terminals = await ctx.ClockInTerminals.Where(x => terminalIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (TerminalFields original in snapshot.Terminals)
        {
            if (terminals.TryGetValue(original.Id, out ClockInTerminal? terminal))
            {
                terminal.CreatedByUserId = original.CreatedByUserId;
                terminal.RevokedByUserId = original.RevokedByUserId;
            }
        }
    }
}

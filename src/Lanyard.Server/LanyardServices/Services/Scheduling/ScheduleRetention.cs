using Lanyard.Application.Services.Chat;
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
        Guid Id, string? UserId, bool IsActive, bool RemovalPending,
        string CreateByUserId, string? UpdateByUserId, string? PublishedByUserId);

    public record TimeEntryFields(
        Guid Id, string UserId, DateTime? ClockOutUtc, ClockMethod? ClockOutMethod, bool NeedsReview, string? ReviewReason,
        string? CreateByUserId, string? UpdateByUserId, string? ApprovedByUserId);

    public record TerminalFields(Guid Id, string CreatedByUserId, string? RevokedByUserId);

    public record ClaimFields(Guid Id, string UserId, ShiftClaimStatus Status, string? DecidedByUserId, DateTime? DecidedUtc, string? DecisionReason);

    // Checklist ticks are kept like time entries (who did the opening checks, when), anonymised.
    public record ChecklistCompletionFields(Guid Id, string UserId, string? UndoneByUserId);

    // Who set up or last changed a checklist or one of its tasks.
    public record ChecklistAuditFields(Guid Id, bool IsTask, string? CreateByUserId, string? UpdateByUserId);

    public record Snapshot(List<ShiftFields> Shifts, List<TimeEntryFields> TimeEntries, List<TerminalFields> Terminals, List<ClaimFields> Claims)
    {
        // Chat history is detached in the same step (ChatRetention), so one snapshot undoes both.
        public ChatRetention.Snapshot Chat { get; init; } = ChatRetention.Snapshot.Empty;

        public List<ChecklistCompletionFields> ChecklistCompletions { get; init; } = [];

        public List<ChecklistAuditFields> ChecklistAudit { get; init; } = [];

        public int Count => Shifts.Count + TimeEntries.Count + Terminals.Count + Claims.Count + Chat.Count
            + ChecklistCompletions.Count + ChecklistAudit.Count;
    }

    private const string Placeholder = ApplicationDbContext.SystemDeletedUserPlaceholderId;

    // gdprErasure: an erasure also empties the direct messages the person sent and the free-text
    // notes on their time entries (the hours stay, anonymised, for payroll). Not restorable, so
    // only GdprService passes it; a plain account delete keeps both.
    public static async Task<Snapshot> DetachUserAsync(ApplicationDbContext ctx, string userId, DateTime nowUtc, bool gdprErasure = false)
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

        List<ShiftClaim> claims = await ctx.ShiftClaims
            .Where(x => x.UserId == userId || x.DecidedByUserId == userId)
            .ToListAsync();

        // Other people's requests tied to this person's: offers on their swaps, the swap they'd
        // agreed to with their offer, and anything involving their future shifts (cancelled below). Loaded
        // up front so the snapshot can put them back if the delete fails.
        List<Guid> ownOpenIds = claims.Where(x => x.UserId == userId && x.IsOpen).Select(x => x.Id).ToList();
        List<Guid> parentIds = claims
            .Where(x => x.UserId == userId && x.Kind == ShiftClaimKind.SwapOffer && x.Status == ShiftClaimStatus.Accepted && x.ParentClaimId != null)
            .Select(x => x.ParentClaimId!.Value)
            .ToList();
        List<Guid> cancelledShiftIds = shifts.Where(x => x.UserId == userId && x.StartUtc > nowUtc).Select(x => x.Id).ToList();

        List<ShiftClaim> linked = await ctx.ShiftClaims
            .Open()
            .Where(x => x.UserId != userId
                && ((x.ParentClaimId != null && ownOpenIds.Contains(x.ParentClaimId.Value))
                    || parentIds.Contains(x.Id)
                    || cancelledShiftIds.Contains(x.ShiftId)
                    || (x.OfferedShiftId != null && cancelledShiftIds.Contains(x.OfferedShiftId.Value))))
            .ToListAsync();

        claims.AddRange(linked.Where(x => claims.All(c => c.Id != x.Id)));

        ChatRetention.Snapshot chat = await ChatRetention.DetachUserAsync(ctx, userId, nowUtc, gdprErasure);

        // Every company's ticks, not just the caller's: a tick left pointing at the account would
        // block its deletion through the Restrict FK.
        List<ChecklistCompletion> completions = await ctx.ChecklistCompletions
            .IgnoreQueryFilters()
            .Where(x => x.UserId == userId || x.UndoneByUserId == userId)
            .ToListAsync();

        List<Checklist> checklists = await ctx.Checklists
            .IgnoreQueryFilters()
            .Where(x => x.CreateByUserId == userId || x.UpdateByUserId == userId)
            .ToListAsync();

        List<ChecklistTask> checklistTasks = await ctx.ChecklistTasks
            .IgnoreQueryFilters()
            .Where(x => x.CreateByUserId == userId || x.UpdateByUserId == userId)
            .ToListAsync();

        Snapshot snapshot = new(
            shifts.Select(x => new ShiftFields(x.Id, x.UserId, x.IsActive, x.RemovalPending, x.CreateByUserId, x.UpdateByUserId, x.PublishedByUserId)).ToList(),
            entries.Select(x => new TimeEntryFields(x.Id, x.UserId, x.ClockOutUtc, x.ClockOutMethod, x.NeedsReview, x.ReviewReason, x.CreateByUserId, x.UpdateByUserId, x.ApprovedByUserId)).ToList(),
            terminals.Select(x => new TerminalFields(x.Id, x.CreatedByUserId, x.RevokedByUserId)).ToList(),
            claims.Select(x => new ClaimFields(x.Id, x.UserId, x.Status, x.DecidedByUserId, x.DecidedUtc, x.DecisionReason)).ToList())
        {
            Chat = chat,
            ChecklistCompletions = completions.Select(x => new ChecklistCompletionFields(x.Id, x.UserId, x.UndoneByUserId)).ToList(),
            ChecklistAudit =
            [
                .. checklists.Select(x => new ChecklistAuditFields(x.Id, false, x.CreateByUserId, x.UpdateByUserId)),
                .. checklistTasks.Select(x => new ChecklistAuditFields(x.Id, true, x.CreateByUserId, x.UpdateByUserId))
            ]
        };

        // An erasure also empties the notes typed when ticking (readings stay: a fridge temperature
        // says nothing about the person).
        foreach (ChecklistCompletion completion in completions)
        {
            if (completion.UserId == userId)
            {
                completion.UserId = Placeholder;

                if (gdprErasure)
                {
                    completion.Note = null;
                }
            }

            if (completion.UndoneByUserId == userId)
            {
                completion.UndoneByUserId = null;
            }
        }

        foreach (Checklist checklist in checklists)
        {
            checklist.CreateByUserId = checklist.CreateByUserId == userId ? null : checklist.CreateByUserId;
            checklist.UpdateByUserId = checklist.UpdateByUserId == userId ? null : checklist.UpdateByUserId;
        }

        foreach (ChecklistTask task in checklistTasks)
        {
            task.CreateByUserId = task.CreateByUserId == userId ? null : task.CreateByUserId;
            task.UpdateByUserId = task.UpdateByUserId == userId ? null : task.UpdateByUserId;
        }

        // Pick-ups, call-offs and swaps are kept as rota history like the shifts, anonymised. Any
        // still waiting are withdrawn: the person can no longer work or give up a shift.
        foreach (ShiftClaim claim in claims)
        {
            if (claim.UserId == userId)
            {
                if (claim.IsOpen)
                {
                    claim.Status = ShiftClaimStatus.Withdrawn;
                    claim.DecidedUtc = nowUtc;
                    claim.DecisionReason = "The account was deleted.";
                }

                claim.UserId = Placeholder;
            }

            if (claim.DecidedByUserId == userId)
            {
                claim.DecidedByUserId = null;
            }
        }

        // A swap whose chosen offer came from this person goes back to collecting offers; anything
        // else tied to them can't happen any more.
        foreach (ShiftClaim claim in linked.Where(x => x.IsOpen))
        {
            if (parentIds.Contains(claim.Id) && !cancelledShiftIds.Contains(claim.ShiftId))
            {
                claim.Status = ShiftClaimStatus.Pending;
                continue;
            }

            claim.Status = ShiftClaimStatus.Withdrawn;
            claim.DecidedUtc = nowUtc;
            claim.DecisionReason = "Someone involved has left.";
        }

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

                if (gdprErasure)
                {
                    entry.Notes = null;
                }
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

        await ChatRetention.RestoreAsync(ctx, snapshot.Chat);

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

        List<Guid> claimIds = snapshot.Claims.Select(x => x.Id).ToList();
        Dictionary<Guid, ShiftClaim> claims = await ctx.ShiftClaims.Where(x => claimIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        foreach (ClaimFields original in snapshot.Claims)
        {
            if (claims.TryGetValue(original.Id, out ShiftClaim? claim))
            {
                claim.UserId = original.UserId;
                claim.Status = original.Status;
                claim.DecidedByUserId = original.DecidedByUserId;
                claim.DecidedUtc = original.DecidedUtc;
                claim.DecisionReason = original.DecisionReason;
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

        List<Guid> completionIds = snapshot.ChecklistCompletions.Select(x => x.Id).ToList();
        Dictionary<Guid, ChecklistCompletion> completions = await ctx.ChecklistCompletions
            .IgnoreQueryFilters()
            .Where(x => completionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        foreach (ChecklistCompletionFields original in snapshot.ChecklistCompletions)
        {
            if (completions.TryGetValue(original.Id, out ChecklistCompletion? completion))
            {
                completion.UserId = original.UserId;
                completion.UndoneByUserId = original.UndoneByUserId;
            }
        }

        List<Guid> checklistIds = snapshot.ChecklistAudit.Where(x => !x.IsTask).Select(x => x.Id).ToList();
        Dictionary<Guid, Checklist> checklists = await ctx.Checklists
            .IgnoreQueryFilters()
            .Where(x => checklistIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        List<Guid> taskIds = snapshot.ChecklistAudit.Where(x => x.IsTask).Select(x => x.Id).ToList();
        Dictionary<Guid, ChecklistTask> checklistTasks = await ctx.ChecklistTasks
            .IgnoreQueryFilters()
            .Where(x => taskIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        foreach (ChecklistAuditFields original in snapshot.ChecklistAudit)
        {
            if (!original.IsTask && checklists.TryGetValue(original.Id, out Checklist? checklist))
            {
                checklist.CreateByUserId = original.CreateByUserId;
                checklist.UpdateByUserId = original.UpdateByUserId;
            }
            else if (original.IsTask && checklistTasks.TryGetValue(original.Id, out ChecklistTask? task))
            {
                task.CreateByUserId = original.CreateByUserId;
                task.UpdateByUserId = original.UpdateByUserId;
            }
        }
    }
}

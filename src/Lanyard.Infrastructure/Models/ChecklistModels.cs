using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.Models
{
    // A routine one location runs on some days: "Opening", "Closing", "Party room reset". Its tasks
    // are ticked off by the people on that day, on the clock-in terminal or their phone. Belongs to
    // one location, like ShiftTemplate, and is archived (IsActive = false) rather than deleted so
    // its history stays readable.
    public class Checklist : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required string Name { get; set; }
        public string? Description { get; set; }

        public int SortOrder { get; set; }

        public ChecklistDays Days { get; set; } = ChecklistDays.EveryDay;

        // Skip days the location has no opening hours for. A location with no opening hours set
        // up at all counts as open every day, the same as the kiosk alert monitor.
        public bool OnlyWhenOpen { get; set; } = true;

        public ChecklistDueRule DueRule { get; set; } = ChecklistDueRule.None;

        // Minutes after opening (AfterOpening) or before closing (BeforeClosing).
        public int DueOffsetMinutes { get; set; }

        // Venue-local time for FixedTime.
        public TimeOnly? DueTime { get; set; }

        // When set, only people who hold this position (plus the location's Managers and Admins)
        // can tick its tasks.
        public Guid? StaffPositionId { get; set; }
        public StaffPosition? StaffPosition { get; set; }

        public bool IsActive { get; set; } = true;

        public List<ChecklistTask> Tasks { get; set; } = [];

        public DateTime CreateDate { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }
    }

    // One step of a checklist, in SortOrder. Archived rather than deleted; a day's run keeps its own
    // copy of the title, so editing or archiving a task never rewrites history.
    public class ChecklistTask
    {
        public Guid Id { get; set; }

        public required Guid ChecklistId { get; set; }
        public Checklist? Checklist { get; set; }

        public required string Title { get; set; }
        public string? Detail { get; set; }

        public ChecklistResponse Response { get; set; } = ChecklistResponse.Tick;

        // For Number: "°C", "kg". Null otherwise.
        public string? Unit { get; set; }

        public int SortOrder { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreateDate { get; set; }
        public string? CreateByUserId { get; set; }
        public DateTime? UpdateDate { get; set; }
        public string? UpdateByUserId { get; set; }
    }

    // A checklist on one venue day: made the first time anyone looks at or ticks it that day, or
    // by the daily sweep, so a day nobody touched still shows as missed. Copies the checklist's
    // name, due time and tasks (Items) as they were, so later edits don't change history.
    // CompanyId is always set from the location: runs are often made by the terminal, which runs
    // as the unfiltered system tenant.
    public class ChecklistRun : ICompanyOwned
    {
        public Guid Id { get; set; }

        public required int CompanyId { get; set; }
        public Company? Company { get; set; }

        public required int LocationId { get; set; }
        public Location? Location { get; set; }

        public required Guid ChecklistId { get; set; }
        public Checklist? Checklist { get; set; }

        public DateOnly VenueDate { get; set; }

        public required string Name { get; set; }

        public DateTime? DueUtc { get; set; }

        // Set when the missed alert is raised, so it's raised once. Claimed with a concurrency
        // check so two sweeps can't both raise it.
        [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
        public DateTime? MissedAlertUtc { get; set; }

        public DateTime CreateDate { get; set; }

        public List<ChecklistRunItem> Items { get; set; } = [];
    }

    // A task as it was when the day's run was made.
    public class ChecklistRunItem
    {
        public Guid Id { get; set; }

        public required Guid RunId { get; set; }
        public ChecklistRun? Run { get; set; }

        // The task it was copied from; null once that task is gone.
        public Guid? TaskId { get; set; }
        public ChecklistTask? Task { get; set; }

        public required string Title { get; set; }
        public string? Detail { get; set; }

        public ChecklistResponse Response { get; set; }
        public string? Unit { get; set; }

        public int SortOrder { get; set; }

        public List<ChecklistCompletion> Completions { get; set; } = [];
    }

    // Someone ticking a task. At most one live (IsActive) tick per item; undoing a tick keeps the
    // row with who undid it, so the history shows both. UserId is a Restrict FK: when an account
    // goes, ScheduleRetention moves the tick to the deleted-user placeholder, like time entries.
    // A tick made on a kiosk dashboard has no person: UserId is null and ClientId names the kiosk.
    public class ChecklistCompletion
    {
        public Guid Id { get; set; }

        public required Guid RunItemId { get; set; }
        public ChecklistRunItem? RunItem { get; set; }

        public string? UserId { get; set; }
        public UserProfile? User { get; set; }

        public Guid? ClientId { get; set; }
        public Client? Client { get; set; }

        public DateTime CompletedUtc { get; set; }

        public ChecklistCompletionMethod Method { get; set; }

        public Guid? ClockInTerminalId { get; set; }
        public ClockInTerminal? ClockInTerminal { get; set; }

        public string? Note { get; set; }

        public decimal? Reading { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime? UndoneUtc { get; set; }
        public string? UndoneByUserId { get; set; }
        public UserProfile? UndoneByUser { get; set; }
    }
}

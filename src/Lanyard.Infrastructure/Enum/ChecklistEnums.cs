namespace Lanyard.Infrastructure.Enum;

// The days a checklist runs on. Stored as its int value, so never renumber a member.
[Flags]
public enum ChecklistDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,

    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekends = Saturday | Sunday,
    EveryDay = Weekdays | Weekends
}

// When a checklist has to be finished by. Opening and closing come from the location's opening
// hours for that day; a fixed time is venue-local wall-clock. Stored, never renumber.
public enum ChecklistDueRule
{
    None = 0,
    AfterOpening = 1,
    BeforeClosing = 2,
    FixedTime = 3
}

// What ticking a task asks for. Stored, never renumber.
public enum ChecklistResponse
{
    // Just a tick.
    Tick = 0,

    // A tick, with a note the person may add ("Mop bucket left in the store room").
    TickWithNote = 1,

    // A number reading with a unit, such as a fridge temperature in °C.
    Number = 2
}

// Where a task was ticked. Stored, never renumber.
public enum ChecklistCompletionMethod
{
    Phone = 1,
    Terminal = 2,

    // A manager ticked it from the Checklists today page.
    Manager = 3,

    // Ticked on a kiosk's dashboard (a Checklist card that allows it). Nobody signs in, so the tick
    // records the kiosk rather than a person.
    Kiosk = 4
}

// Worked out from a run's ticks and due time, never stored.
public enum ChecklistRunStatus
{
    // Nothing ticked yet and it isn't due.
    NotStarted = 1,

    // Some ticked, not all, and it isn't due yet.
    InProgress = 2,

    // Every task ticked.
    Complete = 3,

    // Past its due time (or the day is over) with tasks still to tick.
    Missed = 4
}

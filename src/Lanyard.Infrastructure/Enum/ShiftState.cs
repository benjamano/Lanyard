namespace Lanyard.Infrastructure.Enum;

// Derived from Shift's publish/active fields (see Shift.GetState), never stored.
public enum ShiftState
{
    // Never published - only managers can see it.
    Draft = 0,

    // Published and unchanged since.
    Published = 1,

    // Published, then edited - staff still see it (with its latest times), and the next publish
    // re-notifies them.
    Changed = 2,

    // Was published, then removed; the person hasn't been told yet. Shown struck-through to
    // managers until the next publish clears it.
    RemovedPendingNotice = 3,

    // Gone and nobody needs telling (a removed draft, or a removal already published).
    Removed = 4
}

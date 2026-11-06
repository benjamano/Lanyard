namespace Lanyard.Infrastructure.DTO.Scheduling;

// One thing the rota needs on one day: "Saturday AM needs 4 Hosts 09:00-14:00". Made from a shift
// template, or from a booked party (PartyBookingId set) and its type's staffing rules. Needed can
// be 0 for a template position that's only there for parties and hires to add to. RegularUserIds
// are the template's regulars for the position, given the shift first when they can work it. Needed is how many more shifts it wants once the shifts already on the rota for it
// have been counted.
public sealed record DemandSlot(
    string Name,
    DateOnly Day,
    DateTime StartUtc,
    DateTime EndUtc,
    int BreakMinutes,
    Guid StaffPositionId,
    string PositionName,
    int Needed,
    Guid? TemplateId,
    Guid? PartyBookingId = null,
    IReadOnlyList<string>? RegularUserIds = null);

// One shift Auto-build would make (or made). UserId null = left as an open shift, with WhyOpen
// saying why nobody was picked. Reason is the main reason that person was picked, if any.
public sealed record AutoBuildLine(
    string Name,
    DateOnly Day,
    DateTime StartUtc,
    DateTime EndUtc,
    string PositionName,
    string? UserId,
    string? DisplayName,
    string? Reason,
    string? WhyOpen)
{
    public bool IsOpen => UserId is null;
}

// What Auto-build would do (preview) or did (apply) for a location and some dates.
// AlreadyCovered counts template and party shifts the rota already had, which were left alone.
// Removed are draft shifts it added for a party that has since been cancelled or moved, which it
// takes off; Warnings are published party shifts like that, which only the manager can change.
public sealed record AutoBuildResult(
    string LocationName,
    DateOnly From,
    DateOnly To,
    int TemplateCount,
    int AlreadyCovered,
    List<AutoBuildLine> Lines,
    int PartyCount = 0,
    List<AutoBuildLine>? Removed = null,
    List<string>? Warnings = null)
{
    public List<AutoBuildLine> RemovedLines => Removed ?? [];

    public List<string> WarningLines => Warnings ?? [];

    public bool HasChanges => Lines.Count > 0 || RemovedLines.Count > 0;

    public int Assigned => Lines.Count(x => !x.IsOpen);

    public int Open => Lines.Count(x => x.IsOpen);

    public int PeopleCount => Lines.Where(x => !x.IsOpen).Select(x => x.UserId).Distinct().Count();
}

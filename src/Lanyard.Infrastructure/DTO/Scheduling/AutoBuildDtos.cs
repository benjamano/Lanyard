namespace Lanyard.Infrastructure.DTO.Scheduling;

// One thing the rota needs on one day: "Saturday AM needs 4 Hosts 09:00-14:00". Made from a shift
// template; Needed is how many more shifts it wants once the shifts already on the rota for it
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
    Guid? TemplateId);

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
// AlreadyCovered counts template shifts the rota already had, which were left alone.
public sealed record AutoBuildResult(
    string LocationName,
    DateOnly From,
    DateOnly To,
    int TemplateCount,
    int AlreadyCovered,
    List<AutoBuildLine> Lines)
{
    public int Assigned => Lines.Count(x => !x.IsOpen);

    public int Open => Lines.Count(x => x.IsOpen);

    public int PeopleCount => Lines.Where(x => !x.IsOpen).Select(x => x.UserId).Distinct().Count();
}

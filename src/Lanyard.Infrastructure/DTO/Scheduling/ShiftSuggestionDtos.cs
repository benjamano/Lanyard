using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Scheduling;

// One line on a suggestion card: why someone is a good pick (Positive) or what to watch out for
// (Caution). Kind lets the UI pick an icon and colour; Text is what it says.
public record SuggestionReason(SuggestionReasonKind Kind, string Text)
{
    public bool IsCaution => Kind is SuggestionReasonKind.OverMaxHours or SuggestionReasonKind.PendingTimeOff or SuggestionReasonKind.ShortRest;
}

// One person considered for a shift. Ranked people have Rank 1, 2, 3...; someone who can't work it
// has Rank 0 and an ExcludedReason ("They're already working then."). Hours are company-wide, for
// the Monday week the shift starts in; MaxHours is their contract maximum, if they have one.
public record ShiftSuggestion(
    string UserId,
    string DisplayName,
    string? PositionName,
    int Rank,
    decimal WeekHoursBefore,
    decimal WeekHoursAfter,
    decimal? MaxHours,
    List<SuggestionReason> Reasons,
    string? ExcludedReason)
{
    public bool IsExcluded => ExcludedReason is not null;

    public bool AskedForShift => Reasons.Any(x => x.Kind == SuggestionReasonKind.AskedForShift);

    public IEnumerable<SuggestionReason> Positives => Reasons.Where(x => !x.IsCaution);

    public IEnumerable<SuggestionReason> Cautions => Reasons.Where(x => x.IsCaution);
}

// Who could fill a shift, best first, and who couldn't and why. Advice only: nothing is saved.
public record ShiftSuggestions(List<ShiftSuggestion> Ranked, List<ShiftSuggestion> NotSuggested)
{
    public static ShiftSuggestions Empty { get; } = new([], []);
}

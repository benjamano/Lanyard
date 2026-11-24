using Lanyard.Shared.Projection;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// Keys stored in ProjectionProgramParameterValue.BindingKey. Stable strings rather than enum
/// numbers, so a source can be added (or one retired) without a migration.
/// </summary>
public static class ProjectionBindingKeys
{
    public const string LastGameWinner = "game.last-winner";
    public const string TopScoreToday = "game.top-score-today";
    public const string OpeningHoursToday = "location.opening-hours-today";
    public const string PartyWelcome = "party.welcome";
    public const string PartyChildName = "party.child-name";
    public const string PartyNextUp = "party.next-up";
}

public sealed record ProjectionBindingDescriptor(string Key, string DisplayName, string Description, string DefaultFallback);

/// <summary>
/// What each data source is called and what it falls back to. Static so the offline manifest, the
/// step save validation and the editor can all use it without the resolver; every key here must
/// also have an <see cref="IProjectionBindingSource"/> registered in Program.cs (a test checks).
/// </summary>
public static class ProjectionBindingCatalogue
{
    public static IReadOnlyList<ProjectionBindingDescriptor> All { get; } =
    [
        new(ProjectionBindingKeys.LastGameWinner,
            "Last game winner",
            "Who won the most recent game played today at this kiosk's location.",
            "Who will win the next game?"),

        new(ProjectionBindingKeys.TopScoreToday,
            "Today's top score",
            "The highest individual score of the day at this kiosk's location.",
            "Be the first on today's leaderboard!"),

        new(ProjectionBindingKeys.OpeningHoursToday,
            "Opening hours today",
            "Today's opening hours for this kiosk's location.",
            "Ask a member of staff for today's opening hours."),

        new(ProjectionBindingKeys.PartyWelcome,
            "Party welcome",
            "\"Happy 7th birthday, Ava!\" for the party on now in this kiosk's party room (or at its location), from 15 minutes before it starts. Only the first name, and only when Party settings allows names on screens.",
            "Welcome to the party!"),

        new(ProjectionBindingKeys.PartyChildName,
            "Birthday child's first name",
            "Just the first name of the child whose party is on now in this kiosk's party room (or at its location). Only when Party settings allows names on screens.",
            "Birthday star"),

        new(ProjectionBindingKeys.PartyNextUp,
            "Party: what's next",
            "The next laser tag or food time for the party on now, such as \"Laser tag at 14:30\". Shows no name.",
            "Have a great party!")
    ];

    private static readonly Dictionary<string, ProjectionBindingDescriptor> ByKey =
        All.ToDictionary(x => x.Key, StringComparer.Ordinal);

    public static ProjectionBindingDescriptor? Find(string? key)
    {
        return key is not null && ByKey.TryGetValue(key, out ProjectionBindingDescriptor? descriptor) ? descriptor : null;
    }

    /// <summary>
    /// Only Show Text's Text can be bound. Templates are hard-coded kiosk components, so the set of
    /// parameters that render as plain text is known; Play a Video's Volume is seeded as a String
    /// too, and a "bind any String" rule would offer it.
    /// </summary>
    public static bool IsBindable(string? templateName, string? parameterName)
    {
        return templateName == ProjectionTemplateNames.ShowText && parameterName == ProjectionParameterNames.Text;
    }

    /// <summary>
    /// What a bound parameter shows without live data: the admin's own fallback text, or the
    /// source's default when they left it blank. A value with no binding is returned as it is.
    /// </summary>
    public static string? FallbackFor(string? bindingKey, string? value)
    {
        if (bindingKey is null)
        {
            return value;
        }

        string fallback = ProjectionBindingText.Normalise(value);

        if (fallback.Length > 0)
        {
            return fallback;
        }

        return Find(bindingKey)?.DefaultFallback ?? string.Empty;
    }
}

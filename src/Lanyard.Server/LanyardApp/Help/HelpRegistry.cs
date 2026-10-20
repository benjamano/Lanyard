using System.Security.Claims;
using Lanyard.Infrastructure.Enum;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Lanyard.App.Help;

public enum HelpAudience
{
    Everyone,
    Managers,
    Admins
}

public sealed record HelpTopic(string Key, string Title)
{
    // FluentTab ids on the help page; HelpPage maps ?topic=Key onto this.
    public string TabId => $"help-{Key}";
}

// One help page, always at /help/{Key}. The auth fields must match the page's own [Authorize]/
// [RequiresFeature] - HelpCoverageTests checks they do, so the hub never offers a card that
// leads to an access-denied page.
public sealed record HelpArea(
    string Key,
    string Title,
    string Summary,
    HelpAudience Audience,
    Func<Icon> Icon,
    IReadOnlyList<HelpTopic> Topics,
    // The app routes this help explains, matched by prefix (exact, or followed by "/").
    // HelpCoverageTests fails for any routed page no area covers.
    IReadOnlyList<string> Covers,
    // Comma-separated, same format as [Authorize(Roles = ...)]. Null means any signed-in user.
    string? Roles = null,
    CompanyFeature? Feature = null)
{
    public string Route => $"/help/{Key}";

    public bool IsVisibleTo(ClaimsPrincipal user, IReadOnlySet<CompanyFeature> hiddenFeatures)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (Feature is CompanyFeature feature && hiddenFeatures.Contains(feature))
        {
            return false;
        }

        return Roles is null || Roles.Split(',').Any(role => user.IsInRole(role.Trim()));
    }

    public string UrlFor(string? topic) =>
        topic is not null && Topics.Any(t => t.Key == topic) ? $"{Route}?topic={topic}" : Route;
}

// Every help page in the app. The /help hub, HelpButton and HelpPage all read from here, so adding
// a page means adding its entry - see the help-pages skill.
public static class HelpRegistry
{
    public const string ManageTraining = "training";
    public const string MyTraining = "my-training";

    public static IReadOnlyList<HelpArea> Areas { get; } =
    [
        new(MyTraining,
            "My Training",
            "Taking the courses assigned to you, quizzes, retakes and certificates.",
            HelpAudience.Everyone,
            () => new Icons.Regular.Size20.BookOpen(),
            [
                new("overview", "Overview"),
                new("taking", "Taking a Course"),
                new("results", "Results & Certificates")
            ],
            ["/training"],
            Feature: CompanyFeature.Training),

        new(ManageTraining,
            "Training",
            "Building courses, assigning them to staff and tracking who has completed what.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.HatGraduation(),
            [
                new("overview", "Overview"),
                new("courses", "Creating Courses"),
                new("assigning", "Assigning Training"),
                new("reports", "Tracking Completion"),
                new("locations", "Multi-Location Rules")
            ],
            ["/manage/training"],
            Roles: "Admin,Manager",
            Feature: CompanyFeature.Training)
    ];

    public static HelpArea Get(string key) =>
        Areas.FirstOrDefault(a => a.Key == key)
        ?? throw new ArgumentException($"No help area registered with key '{key}'.", nameof(key));
}

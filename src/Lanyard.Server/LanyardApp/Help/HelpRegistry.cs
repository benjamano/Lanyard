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
    public const string Rota = "rota";
    public const string MyShifts = "my-shifts";
    public const string Users = "users";
    public const string Compliance = "compliance";
    public const string Onboarding = "onboarding";
    public const string Account = "account";

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

        new(MyShifts,
            "My Shifts",
            "Your shifts, swapping and picking up shifts, time off, availability and clocking in.",
            HelpAudience.Everyone,
            () => new Icons.Regular.Size20.CalendarLtr(),
            [
                new("overview", "Overview"),
                new("changes", "Swaps & Open Shifts"),
                new("time-off", "Time Off"),
                new("availability", "Availability"),
                new("clocking", "Clocking In")
            ],
            ["/rota"],
            Feature: CompanyFeature.StaffScheduling),

        new(Rota,
            "Rota",
            "Building and publishing the rota, shift requests, time off, timesheets and payroll export.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.People(),
            [
                new("overview", "Overview"),
                new("building", "Building the Rota"),
                new("requests", "Shift Requests"),
                new("time-off", "Time Off"),
                new("timesheets", "Timesheets & Payroll"),
                new("setup", "Setting Up")
            ],
            ["/manage/rota"],
            Roles: "Admin,Manager",
            Feature: CompanyFeature.StaffScheduling),

        new(Account,
            "Your Account",
            "Your details, notifications, password, Face ID or fingerprint sign-in and two-factor authentication.",
            HelpAudience.Everyone,
            () => new Icons.Regular.Size20.Person(),
            [
                new("details", "Your Details"),
                new("notifications", "Notifications"),
                new("sign-in", "Signing In Securely")
            ],
            ["/account"]),

        new(Users,
            "Users & Roles",
            "Adding staff, their profiles, roles and permissions, locations, company features and sign-in security.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.PeopleTeam(),
            [
                new("overview", "Users"),
                new("profile", "A Person's Profile"),
                new("roles", "Roles & Permissions"),
                new("locations", "Companies & Locations"),
                new("security", "Sign-in & Security")
            ],
            ["/manage/users", "/manage/roles", "/manage/companies-locations"],
            Roles: "Admin,Manager"),

        new(Compliance,
            "Compliance",
            "Overdue training and expired or expiring staff documents, and how to clear them.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.ShieldCheckmark(),
            [
                new("overview", "Overview")
            ],
            ["/manage/compliance"],
            Roles: "Admin,Manager"),

        new(Onboarding,
            "Onboarding & Staff Documents",
            "The welcome email for new starters, and keeping staff documents such as certificates up to date.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.DocumentBulletList(),
            [
                new("welcome", "Welcome Email"),
                new("documents", "Staff Documents")
            ],
            ["/manage/onboarding", "/manage/staff-documents"],
            Roles: "Admin,Manager",
            Feature: CompanyFeature.Onboarding),

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

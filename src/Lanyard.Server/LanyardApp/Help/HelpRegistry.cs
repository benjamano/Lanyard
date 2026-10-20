using System.Security.Claims;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
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
    public const string Chat = "chat";
    public const string ChatModeration = "chat-moderation";
    public const string Announcements = "announcements";
    public const string Files = "files";
    public const string Dashboards = "dashboards";
    public const string Music = "music";
    public const string Projection = "projection";
    public const string Automation = "automation";
    public const string Dmx = "dmx";
    public const string Kiosks = "kiosks";
    public const string Insights = "insights";
    public const string Feedback = "feedback";

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

        new(Chat,
            "Chat",
            "Messaging colleagues, groups and channels, replying and editing, and reporting or blocking someone.",
            HelpAudience.Everyone,
            () => new Icons.Regular.Size20.Chat(),
            [
                new("conversations", "Conversations"),
                new("messages", "Messages"),
                new("safety", "Reporting & Blocking")
            ],
            ["/chat"],
            Feature: CompanyFeature.Chat),

        new(Dashboards,
            "Dashboards",
            "Choosing your home screen, building dashboards, and what each widget shows.",
            HelpAudience.Everyone,
            () => new Icons.Regular.Size20.DataArea(),
            [
                new("overview", "Overview"),
                new("editing", "Editing a Dashboard"),
                new("widgets", "Widgets")
            ],
            ["/manage/dashboards"]),

        new(ChatModeration,
            "Chat Channels & Reports",
            "Looking after location channels, pinned posts, and dealing with reported messages.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.ChatWarning(),
            [
                new("channels", "Channels"),
                new("reports", "Reports")
            ],
            ["/manage/chat"],
            Roles: "Admin,Manager",
            Feature: CompanyFeature.Chat),

        new(Announcements,
            "Announcements",
            "Posting notices to staff at a location, pinning, expiry and emailing them out.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.Megaphone(),
            [
                new("overview", "Announcements")
            ],
            ["/manage/announcements"],
            Roles: "Admin,CanPostAnnouncements",
            Feature: CompanyFeature.Announcements),

        new(Files,
            "File Manager",
            "Folders, uploading, and moving, renaming and deleting your company's files.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.Folder(),
            [
                new("overview", "File Manager")
            ],
            ["/files"],
            Roles: "Admin,CanManageFiles"),

        new(Music,
            "Music & Audio Clips",
            "Playing music on a kiosk, the music schedule for each time of day, and audio clips over the music.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.MusicNote2(),
            [
                new("player", "Music"),
                new("schedule", "Music Schedule"),
                new("clips", "Audio Clips")
            ],
            ["/music", "/manage/music-schedule", "/manage/audio-clips"],
            Roles: "Admin,Manager,CanControlMusic,CanPlayAudioClips"),

        new(Projection,
            "Projection",
            "Building projection programs, controlling what's on screen, and step templates.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.ProjectionScreenText(),
            [
                new("programs", "Programs"),
                new("control", "Projection Control"),
                new("templates", "Templates")
            ],
            ["/manage/projectionprograms", "/manage/projectioncontrol", "/manage/projectiontemplates"],
            Roles: "Admin,Manager"),

        new(Automation,
            "Automation",
            "Rules that run music, projection, lighting and audio clips by themselves, and the execution log.",
            HelpAudience.Admins,
            () => new Icons.Regular.Size20.SettingsCogMultiple(),
            [
                new("rules", "Rules & Triggers"),
                new("conditions", "Conditions & Actions"),
                new("log", "Execution Log")
            ],
            ["/automation"],
            Roles: "Admin"),

        new(Dmx,
            "DMX Lighting",
            "The DMX desk, setting up fixtures, and building and running lighting scenes.",
            HelpAudience.Admins,
            () => new Icons.Regular.Size20.Lightbulb(),
            [
                new("desk", "The Desk"),
                new("fixtures", "Fixtures"),
                new("scenes", "Scenes")
            ],
            ["/dmx"],
            Roles: "Admin,CanManageDmxSystems"),

        new(Kiosks,
            "Kiosks",
            "Kiosk health and alerts, restarting, diagnostics, each kiosk's settings, and software updates.",
            HelpAudience.Admins,
            () => new Icons.Regular.Size20.Laptop(),
            [
                new("overview", "Kiosks & Health"),
                new("settings", "Kiosk Settings"),
                new("updates", "Updates & Releases")
            ],
            ["/manage/clients", "/manage/client-releases"],
            Roles: "Admin"),

        new(Insights,
            "Insights",
            "Comparing games played with staff hours, and the hours where staffing didn't match demand.",
            HelpAudience.Managers,
            () => new Icons.Regular.Size20.DataArea(),
            [
                new("overview", "Figures"),
                new("week", "Typical Week & Flags")
            ],
            ["/manage/insights"],
            Roles: "Admin,Manager",
            Feature: CompanyFeature.Insights),

        new(Feedback,
            "Lanyard Feedback",
            "Reading and replying to the thumbs and star ratings staff send about Lanyard.",
            HelpAudience.Admins,
            () => new Icons.Regular.Size20.PersonFeedback(),
            [
                new("inbox", "Feedback Inbox")
            ],
            ["/manage/feedback"],
            Roles: LanyardRoles.PlatformAdmin),

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

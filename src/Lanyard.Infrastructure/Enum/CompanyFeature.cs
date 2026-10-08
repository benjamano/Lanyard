namespace Lanyard.Infrastructure.Enum;

// A paid-for module an admin can switch on or off per company (Manage > Companies & Locations).
// Stored as its int value in CompanyFeatureSetting.Feature, so never renumber an existing member.
public enum CompanyFeature
{
    // Courses, My Training, the training dashboard and the My Training widget.
    Training = 1,

    // Rota, My Shifts, open shifts, time off, timesheets and clock-in terminals.
    StaffScheduling = 2,

    // Staff chat, channels and chat moderation.
    Chat = 3,

    // Onboarding emails and staff document types.
    Onboarding = 4,

    // The Announcements page and widget.
    Announcements = 5,

    // Party bookings and the documents printed for each party.
    Parties = 6
}

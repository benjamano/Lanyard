using Lanyard.Infrastructure.DTO.Notifications;

namespace Lanyard.Application.Services.Notifications;

// Wording for a sent-out announcement, shared by the email and the push.
public static class AnnouncementNotices
{
    // Staff read announcements through the dashboard widget on their home page.
    public const string HomeUrl = "/";

    public const int PushBodyLength = 140;

    public static Notice For(AnnouncementPayload payload)
    {
        // One email paragraph per paragraph the manager typed, so the email reads as they wrote it.
        List<string> lines = payload.Body
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        lines.Add(string.IsNullOrWhiteSpace(payload.AuthorName)
            ? $"Posted for {payload.LocationName}."
            : $"Posted by {payload.AuthorName} for {payload.LocationName}.");

        return new Notice(payload.Title, lines, HomeUrl, "Open Lanyard", $"announcement-{payload.AnnouncementId:N}");
    }
}

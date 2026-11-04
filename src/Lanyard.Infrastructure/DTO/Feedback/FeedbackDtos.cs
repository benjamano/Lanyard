using Lanyard.Infrastructure.Enum;

namespace Lanyard.Infrastructure.DTO.Feedback;

// What the header button or the star prompt sends. PagePath is base-relative ("rota?week=3").
public record FeedbackSubmission(
    string UserId,
    int? CompanyId,
    int? LocationId,
    FeedbackKind Kind,
    int Score,
    string? Comment,
    string PagePath);

// One card in the platform admin's feedback inbox.
public record FeedbackInboxItem(
    Guid Id,
    FeedbackKind Kind,
    int Score,
    string? Comment,
    string PagePath,
    string UserName,
    string? UserEmail,
    string? CompanyName,
    string? LocationName,
    DateTime CreatedUtc,
    DateTime? ReviewedUtc);

public enum FeedbackInboxFilter
{
    All,
    ThumbsUp,
    ThumbsDown,
    Stars
}

// Everything the "new feedback" email shows, already resolved to names.
public record FeedbackEmail(
    FeedbackKind Kind,
    int Score,
    string? Comment,
    string PageUrl,
    string SenderName,
    string? SenderEmail,
    string? CompanyName,
    string? LocationName,
    string InboxUrl);

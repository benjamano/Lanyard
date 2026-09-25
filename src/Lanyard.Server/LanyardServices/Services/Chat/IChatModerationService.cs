using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Chat;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Chat;

// Reporting a chat message and what managers can do about it. A report carries a snapshot of the
// one message reported - the only way anyone other than a conversation's members ever sees any of
// it - and goes to the managers of the reporter's location.
public interface IChatModerationService
{
    // alsoBlock: for a direct message, also blocks the author.
    Task<Result<ChatReport>> ReportAsync(string reporterUserId, Guid messageId, ChatReportReason reason, string? details, bool alsoBlock);

    // Reports about the viewer themselves are left out (unless they're an Admin): a reported
    // manager mustn't learn who reported them.
    Task<Result<List<ChatReportView>>> GetReportsAsync(LocationScope scope, int locationId, bool openOnly, string? viewerUserId);

    // Dismiss (neither), or take action: remove the message for everyone and/or suspend its author
    // from posting. suspendDays null with suspend = until a manager lifts it.
    Task<Result<bool>> ResolveAsync(LocationScope scope, Guid reportId, bool removeMessage, bool suspend, int? suspendDays, string? outcome, string reviewerUserId);

    Task<Result<List<ChatSuspensionView>>> GetSuspensionsAsync(LocationScope scope, int companyId);

    Task<Result<bool>> LiftSuspensionAsync(LocationScope scope, Guid suspensionId, string userId);

    // Open reports at the manager's signed-in location, for the nav badge.
    Task<Result<int>> CountOpenForNavAsync(LocationScope scope, string? viewerUserId);
}

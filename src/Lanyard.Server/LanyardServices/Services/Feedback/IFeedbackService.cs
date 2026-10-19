using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.DTO.Feedback;
using Lanyard.Infrastructure.Models;

namespace Lanyard.Application.Services.Feedback;

// Product feedback from staff (the header thumbs button and the occasional star prompt), emailed
// to Lanyard's platform admins and kept for their inbox.
public interface IFeedbackService
{
    Task<Result<UserFeedback>> SubmitAsync(FeedbackSubmission submission);

    // Platform admins only - every company's feedback, newest first.
    Task<Result<List<FeedbackInboxItem>>> GetInboxAsync(FeedbackInboxFilter filter, bool unreviewedOnly);

    // Platform admins only - for the nav badge.
    Task<Result<int>> CountUnreviewedAsync();

    // Platform admins only. reviewed: false puts it back in the unreviewed list.
    Task<Result<bool>> SetReviewedAsync(Guid feedbackId, bool reviewed);
}

namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber.
public enum FeedbackKind
{
    // The header button: Score is +1 (thumbs up) or -1 (thumbs down).
    Thumbs = 0,

    // The occasional corner prompt: Score is 1-5 stars.
    StarRating = 1
}

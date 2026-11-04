namespace Lanyard.Application.Services.Feedback;

public enum FeedbackPromptOutcome
{
    // It appeared. Counts as asking, so someone who ignores it isn't asked again on the next page.
    Shown,

    // "Not now" or the close button.
    Dismissed,

    // "Don't ask again".
    OptOut
}

// When the occasional "How would you rate Lanyard?" prompt appears. Pure, so the timings are
// testable on their own; FeedbackService supplies the inputs.
public static class FeedbackPromptRules
{
    // Give someone a week to find their feet before asking what they think.
    public static readonly TimeSpan MinAccountAge = TimeSpan.FromDays(7);

    // After they've given a star rating: about once a month.
    public static readonly TimeSpan AfterRating = TimeSpan.FromDays(30);

    // After it was shown and not answered (ignored, closed or "Not now").
    public static readonly TimeSpan AfterPrompt = TimeSpan.FromDays(7);

    public static bool ShouldPrompt(bool optedOut, DateTime? accountStartedUtc, DateTime? lastPromptUtc, DateTime? lastRatingUtc, DateTime nowUtc)
    {
        if (optedOut)
        {
            return false;
        }

        if (accountStartedUtc is DateTime started && nowUtc - started < MinAccountAge)
        {
            return false;
        }

        if (lastRatingUtc is DateTime rated && nowUtc - rated < AfterRating)
        {
            return false;
        }

        if (lastPromptUtc is DateTime prompted && nowUtc - prompted < AfterPrompt)
        {
            return false;
        }

        return true;
    }
}
